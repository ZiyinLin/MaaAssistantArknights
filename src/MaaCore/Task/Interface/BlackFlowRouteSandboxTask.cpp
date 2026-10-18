#include "BlackFlowRouteSandboxTask.h"

#include <charconv>
#include <chrono>
#include <cstdint>
#include <exception>
#include <ranges>
#include <stdexcept>
#include <unordered_map>
#include <utility>
#include <vector>

#include "MaaUtils/NoWarningCV.hpp"

#include "Controller/Controller.h"
#include "Utils/Logger.hpp"

namespace asst
{
namespace
{
using namespace std::chrono_literals;

std::optional<blackflow::NodeId> parse_node_id(const json::value& object, const std::string& key)
{
    if (!object.contains(key)) {
        return std::nullopt;
    }
    const json::value& value = object.at(key);
    if (!value.is_number() && !value.is_string()) {
        return std::nullopt;
    }
    const std::string text = value.is_number() ? value.to_string() : value.as_string();
    blackflow::NodeId id = blackflow::InvalidNodeId;
    const auto [end, error] = std::from_chars(text.data(), text.data() + text.size(), id);
    if (error != std::errc() || end != text.data() + text.size()) {
        return std::nullopt;
    }
    return id;
}

bool parse_steps(const json::value& command, std::vector<blackflow::DraftStep>& steps, std::string& error)
{
    const auto values = command.find<json::array>("steps");
    if (!values.has_value()) {
        steps.clear();
        return true;
    }

    steps.clear();
    steps.reserve(values->size());
    for (std::size_t index = 0; index < values->size(); ++index) {
        const json::value& value = values->at(index);
        if (!value.is_object()) {
            error = "draft step " + std::to_string(index + 1) + " is not an object";
            return false;
        }
        const std::string movement_code = value.get("movement_kind", std::string());
        const auto movement = blackflow::movement_kind_from_code(movement_code);
        if (!movement.has_value()) {
            error = "draft step " + std::to_string(index + 1) + " has an unknown movement kind";
            return false;
        }
        const auto target = parse_node_id(value, "target_node_id");
        const auto assumed_landing = parse_node_id(value, "assumed_landing_node_id");
        if (!target.has_value() && !assumed_landing.has_value()) {
            error = "draft step " + std::to_string(index + 1) + " has no target node";
            return false;
        }
        steps.emplace_back(
            blackflow::DraftStep {
                *movement,
                target.has_value() ? *target : *assumed_landing,
                assumed_landing,
            });
    }
    return true;
}

bool parse_movement_charges(
    const json::value& command,
    std::unordered_map<blackflow::MovementKind, int>& charges,
    std::string& error)
{
    charges.clear();
    const auto values = command.find<json::object>("movement_charges");
    if (!values.has_value()) {
        return true;
    }
    for (const auto& [code, value] : *values) {
        const auto movement = blackflow::movement_kind_from_code(code);
        if (!movement.has_value() || *movement == blackflow::MovementKind::Walk) {
            error = "movement_charges contains an unknown or non-charge movement kind: " + code;
            return false;
        }
        if (!value.is_number() || value.as_integer() < 0 || value.as_integer() > 99) {
            error = "movement charge must be an integer from 0 to 99: " + code;
            return false;
        }
        charges.insert_or_assign(*movement, static_cast<int>(value.as_integer()));
    }
    return true;
}

std::string json_node_id(blackflow::NodeId id)
{
    return std::to_string(id);
}

std::string base64_encode(const std::vector<unsigned char>& data)
{
    static constexpr char Alphabet[] = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    std::string result;
    result.reserve(((data.size() + 2) / 3) * 4);
    for (std::size_t index = 0; index < data.size(); index += 3) {
        const std::uint32_t first = data[index];
        const std::uint32_t second = index + 1 < data.size() ? data[index + 1] : 0;
        const std::uint32_t third = index + 2 < data.size() ? data[index + 2] : 0;
        const std::uint32_t packed = (first << 16U) | (second << 8U) | third;
        result.push_back(Alphabet[(packed >> 18U) & 0x3FU]);
        result.push_back(Alphabet[(packed >> 12U) & 0x3FU]);
        result.push_back(index + 1 < data.size() ? Alphabet[(packed >> 6U) & 0x3FU] : '=');
        result.push_back(index + 2 < data.size() ? Alphabet[packed & 0x3FU] : '=');
    }
    return result;
}

std::string encode_roi_png(const cv::Mat& image, const cv::Rect& roi)
{
    if (roi.x < 0 || roi.y < 0 || roi.width <= 0 || roi.height <= 0 || roi.x + roi.width > image.cols ||
        roi.y + roi.height > image.rows) {
        throw std::runtime_error("route sandbox ROI is outside the recognized frame");
    }
    std::vector<unsigned char> encoded;
    if (!cv::imencode(".png", image(roi), encoded)) {
        throw std::runtime_error("route sandbox ROI could not be encoded");
    }
    return base64_encode(encoded);
}
} // namespace

BlackFlowRouteSandboxTask::BlackFlowRouteSandboxTask(const AsstCallback& callback, Assistant* inst) :
    InterfaceTask(callback, inst, TaskType)
{
    set_retry_times(0);
}

bool BlackFlowRouteSandboxTask::run()
{
    return AbstractTask::run();
}

bool BlackFlowRouteSandboxTask::set_params(const json::value& params)
{
    if (!params.is_object()) {
        Log.error("BlackFlow route sandbox params must be an object");
        return false;
    }
    const std::string command = params.get("command", std::string());
    if (command != "open" && command != "refresh" && command != "evaluate" && command != "close") {
        Log.error("BlackFlow route sandbox received an unsupported command", command);
        return false;
    }
    {
        std::lock_guard lock(m_command_mutex);
        m_commands.emplace_back(params);
    }
    m_command_ready.notify_one();
    return true;
}

bool BlackFlowRouteSandboxTask::_run()
{
    while (true) {
        if (need_exit()) {
            return false;
        }

        json::value command;
        {
            std::unique_lock lock(m_command_mutex);
            m_command_ready.wait_for(lock, 100ms, [this]() { return !m_commands.empty(); });
            if (m_commands.empty()) {
                continue;
            }
            command = std::move(m_commands.front());
            m_commands.pop_front();
        }

        const std::string request_id = command.get("request_id", std::string());
        const std::string name = command.get("command", std::string());
        try {
            if (name == "close") {
                auto info = basic_info_with_what("BlackFlowRouteSandbox");
                info["details"] = json::object {
                    { "request_id", request_id },
                    { "status", "ok" },
                    { "closed", true },
                };
                callback(AsstMsg::SubTaskExtraInfo, info);
                return true;
            }
            if (name == "open" || name == "refresh") {
                handle_refresh(command, request_id);
                continue;
            }
            handle_evaluate(command, request_id);
        }
        catch (const std::exception& exception) {
            emit_error(request_id, "route sandbox command failed: " + std::string(exception.what()));
        }
        catch (...) {
            emit_error(request_id, "route sandbox command failed: unknown exception");
        }
    }
}

void BlackFlowRouteSandboxTask::handle_refresh(const json::value&, const std::string& request_id)
{
    const cv::Mat image = ctrler()->get_image();
    blackflow::SandboxMapSnapshot next;
    std::string error;
    if (!m_observation_service.recognize(image, next, &error)) {
        emit_error(request_id, std::move(error));
        return;
    }
    const std::string image_base64 = encode_roi_png(image, next.roi);
    m_snapshot = std::move(next);
    Log.info(
        "BlackFlow route sandbox snapshot refreshed",
        "snapshot",
        m_snapshot->snapshot_id,
        "floor",
        m_snapshot->floor,
        "nodes",
        m_snapshot->nodes.size(),
        "action_points",
        m_snapshot->action_points.value_or(-1));
    emit_snapshot(request_id, *m_snapshot, image_base64);
}

void BlackFlowRouteSandboxTask::handle_evaluate(const json::value& command, const std::string& request_id)
{
    if (!m_snapshot.has_value()) {
        emit_error(request_id, "route sandbox has no recognized map snapshot");
        return;
    }
    const std::string snapshot_id = command.get("snapshot_id", std::string());
    if (!snapshot_id.empty() && snapshot_id != m_snapshot->snapshot_id) {
        emit_error(request_id, "route sandbox evaluation references a stale map snapshot");
        return;
    }

    const int recognized_action_points = m_snapshot->action_points.value_or(-1);
    const int action_points = command.get("initial_action_points", recognized_action_points);
    if (action_points < 0 || action_points > 64) {
        emit_error(request_id, "route sandbox has no valid recognized or manually supplied action point value");
        return;
    }

    blackflow::RunState initial_state;
    initial_state.floor = m_snapshot->floor;
    initial_state.current_node = m_snapshot->current_node;
    initial_state.resources.action_points = action_points;
    std::string parse_error;
    if (!parse_movement_charges(command, initial_state.resources.movement_charges, parse_error)) {
        emit_error(request_id, std::move(parse_error));
        return;
    }

    std::vector<blackflow::DraftStep> steps;
    if (!parse_steps(command, steps, parse_error)) {
        emit_error(request_id, std::move(parse_error));
        return;
    }

    std::optional<blackflow::MovementKind> next_movement;
    std::string next_code = command.get("movement_kind", std::string());
    if (next_code.empty()) {
        next_code = command.get("next_movement_kind", std::string());
    }
    if (!next_code.empty()) {
        next_movement = blackflow::movement_kind_from_code(next_code);
        if (!next_movement.has_value()) {
            emit_error(request_id, "route sandbox evaluation has an unknown next movement kind");
            return;
        }
    }

    blackflow::DraftEvaluation evaluation;
    std::string evaluation_error;
    const blackflow::GraphLayer graph_layer = command.get("include_inferred_edges", false)
                                                   ? blackflow::GraphLayer::Relaxed
                                                   : blackflow::GraphLayer::Confirmed;
    if (!m_evaluator.evaluate(
            m_snapshot->map,
            std::move(initial_state),
            steps,
            next_movement,
            graph_layer,
            evaluation,
            &evaluation_error)) {
        emit_error(request_id, std::move(evaluation_error));
        return;
    }
    emit_evaluation(request_id, m_snapshot->snapshot_id, evaluation);
}

void BlackFlowRouteSandboxTask::emit_error(const std::string& request_id, std::string message)
{
    Log.warn("BlackFlow route sandbox command failed", "request_id", request_id, "error", message);
    auto info = basic_info_with_what("BlackFlowRouteSandbox");
    info["details"] = json::object {
        { "request_id", request_id },
        { "status", "error" },
        { "error", std::move(message) },
    };
    callback(AsstMsg::SubTaskExtraInfo, info);
}

void BlackFlowRouteSandboxTask::emit_snapshot(
    const std::string& request_id,
    const blackflow::SandboxMapSnapshot& snapshot,
    const std::string& image_base64)
{
    json::array nodes;
    for (const blackflow::SandboxMapNode& node : snapshot.nodes) {
        nodes.emplace_back(
            json::object {
                { "node_id", json_node_id(node.id) },
                { "x", node.x },
                { "y", node.y },
                { "row", node.row },
                { "column", node.column },
                { "type", node.type },
                { "name", node.name },
                { "label", node.name.empty() ? node.type : node.name },
                { "current", node.current },
            });
    }

    json::object snapshot_payload {
        { "snapshot_id", snapshot.snapshot_id },
        { "floor", snapshot.floor },
        { "action_points", snapshot.action_points.value_or(-1) },
        { "action_points_recognized", snapshot.action_points.has_value() },
        { "current_node_id", json_node_id(snapshot.current_node) },
        { "image_base64", image_base64 },
        { "roi",
          json::object {
              { "x", snapshot.roi.x },
              { "y", snapshot.roi.y },
              { "width", snapshot.roi.width },
              { "height", snapshot.roi.height },
          } },
        { "roi_width", snapshot.roi.width },
        { "roi_height", snapshot.roi.height },
        { "nodes", std::move(nodes) },
    };
    auto info = basic_info_with_what("BlackFlowRouteSandbox");
    info["details"] = json::object {
        { "request_id", request_id },
        { "snapshot_id", snapshot.snapshot_id },
        { "status", "ok" },
        { "snapshot", std::move(snapshot_payload) },
    };
    callback(AsstMsg::SubTaskExtraInfo, info);
}

void BlackFlowRouteSandboxTask::emit_evaluation(
    const std::string& request_id,
    const std::string& snapshot_id,
    const blackflow::DraftEvaluation& evaluation)
{
    json::array steps;
    for (const blackflow::ValidatedDraftStep& step : evaluation.steps) {
        json::array path;
        for (const blackflow::NodeId path_node : step.path) {
            path.emplace_back(json_node_id(path_node));
        }
        steps.emplace_back(
            json::object {
                { "movement_kind", std::string(blackflow::movement_kind_code(step.movement)) },
                { "source_node_id", json_node_id(step.source) },
                { "target_node_id", json_node_id(step.target) },
                { "landing_node_id", json_node_id(step.landing) },
                { "path_node_ids", std::move(path) },
                { "action_point_cost", step.action_point_cost },
                { "action_point_gain", step.action_point_gain },
                { "remaining_action_points", step.remaining_action_points },
                { "valid", step.valid },
                { "error", step.error },
                { "uncertain_landing", step.uncertain_landing },
                { "uses_inferred_edge", step.uses_inferred_edge },
                { "predicted", true },
            });
    }

    json::array candidates;
    for (const blackflow::DraftCandidate& candidate : evaluation.candidates) {
        json::array path;
        for (const blackflow::NodeId path_node : candidate.path) {
            path.emplace_back(json_node_id(path_node));
        }
        json::array possible_landings;
        for (const blackflow::NodeId landing : candidate.possible_landings) {
            possible_landings.emplace_back(json_node_id(landing));
        }
        candidates.emplace_back(
            json::object {
                { "movement_kind", std::string(blackflow::movement_kind_code(candidate.movement)) },
                { "source_node_id", json_node_id(candidate.source) },
                { "target_node_id", json_node_id(candidate.target) },
                { "landing_node_id", json_node_id(candidate.landing) },
                { "path_node_ids", std::move(path) },
                { "possible_landing_node_ids", std::move(possible_landings) },
                { "action_point_cost", candidate.action_point_cost },
                { "action_point_gain", candidate.action_point_gain },
                { "controllable", candidate.controllable },
                { "uncertain_landing", candidate.uncertain_landing },
                { "uses_unconfirmed_edge", candidate.uses_unconfirmed_edge },
                { "uses_inferred_edge", candidate.uses_inferred_edge },
                { "predicted", true },
            });
    }

    json::object evaluation_payload {
        { "snapshot_id", snapshot_id },
        { "validated_steps", std::move(steps) },
        { "remaining_action_points", evaluation.final_state.resources.action_points },
        { "current_node_id", json_node_id(evaluation.final_state.current_node) },
        { "invalid_step_index",
          evaluation.invalid_step_index.has_value() ? static_cast<std::int64_t>(*evaluation.invalid_step_index) : -1 },
        { "candidates", std::move(candidates) },
    };
    auto info = basic_info_with_what("BlackFlowRouteSandbox");
    info["details"] = json::object {
        { "request_id", request_id },
        { "snapshot_id", snapshot_id },
        { "status", "ok" },
        { "evaluation", std::move(evaluation_payload) },
    };
    callback(AsstMsg::SubTaskExtraInfo, info);
}
} // namespace asst