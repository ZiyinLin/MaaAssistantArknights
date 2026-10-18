#include "BlackFlowSandboxObservationService.h"

#include <algorithm>
#include <cmath>
#include <exception>
#include <ranges>
#include <string_view>
#include <tuple>
#include <utility>

#include "BlackFlowObservation.h"
#include "Config/Roguelike/BlackFlow/BlackFlowMapPerceptionResource.h"
#include "Config/TaskData.h"
#include "Utils/StringMisc.hpp"
#include "Vision/OCRer.h"
#include "Vision/Roguelike/BlackFlow/BlackFlowFloor.h"

namespace asst::blackflow
{
namespace
{
constexpr std::string_view FloorTask = "BlackFlowRouteSandbox@Floor";
constexpr std::string_view ActionPointsTask = "BlackFlowRouteSandbox@ActionPoints";

void set_error(std::string* error, std::string message)
{
    if (error != nullptr) {
        *error = std::move(message);
    }
}

Rect visual_rect(const perception::Node& node, const cv::Size& bounds)
{
    const int left = static_cast<int>(std::floor(node.visual_center.x - node.visual_half_width));
    const int top = static_cast<int>(std::floor(node.visual_center.y - node.visual_half_height));
    const int right = static_cast<int>(std::ceil(node.visual_center.x + node.visual_half_width));
    const int bottom = static_cast<int>(std::ceil(node.visual_center.y + node.visual_half_height));
    const cv::Rect clipped = cv::Rect(left, top, std::max(1, right - left), std::max(1, bottom - top)) &
                             cv::Rect(0, 0, bounds.width, bounds.height);
    return { clipped.x, clipped.y, clipped.width, clipped.height };
}
} // namespace

bool BlackFlowSandboxObservationService::recognize(
    const cv::Mat& image,
    SandboxMapSnapshot& snapshot,
    std::string* error)
{
    if (image.empty() || image.type() != CV_8UC3) {
        set_error(error, "route sandbox requires a non-empty BGR8 screenshot");
        return false;
    }

    std::string floor_error;
    const auto floor = recognize_floor(image, &floor_error);
    if (!floor.has_value()) {
        set_error(error, std::move(floor_error));
        return false;
    }
    if (!perception::floor_profile(*floor).has_value()) {
        set_error(
            error,
            "route sandbox recognized the terminal sixth page, which has no supported map grid");
        return false;
    }
    if (!prepare(error)) {
        return false;
    }

    perception::MapRecognitionResult result;
    try {
        result = m_analyzer->recognize(image, *floor, false);
    }
    catch (const std::exception& exception) {
        set_error(error, "route sandbox map recognition failed: " + std::string(exception.what()));
        return false;
    }
    catch (...) {
        set_error(error, "route sandbox map recognition failed: unknown exception");
        return false;
    }
    if (!result.ok) {
        set_error(error, "route sandbox map recognition failed: " + result.error);
        return false;
    }
    if (!result.edge_detection.graph_connected) {
        set_error(error, "route sandbox rejected a disconnected recognized map graph");
        return false;
    }

    const cv::Rect map_roi = m_analyzer->map_roi() &
                             cv::Rect(0, 0, result.normalized_bgr.cols, result.normalized_bgr.rows);
    if (map_roi.empty()) {
        set_error(error, "route sandbox map ROI is empty after frame normalization");
        return false;
    }

    BlackFlowMapObservation observation;
    observation.sequence = ++m_sequence;
    observation.viewport_revision = observation.sequence;
    observation.observation_id = "BF-S" + std::to_string(observation.sequence);
    observation.floor = *floor;
    observation.floor_from_ocr = true;
    observation.hud_action_points = recognize_action_points(image);
    observation.coverage = ObservationCoverage::FullMap;
    observation.covered_positions = BlackFlowObservationAdapter::expected_grid_positions(*floor);
    observation.recognition_ok = true;
    observation.graph_connected = true;
    observation.current_marker_temporary_id = result.node_detection.current_marker_node_id;
    observation.current_marker_score = result.node_detection.current_marker_score;
    observation.attempt_count = 1;

    observation.nodes.reserve(result.node_detection.nodes.size());
    for (const perception::Node& node : result.node_detection.nodes) {
        observation.nodes.emplace_back(
            PerceptionNodeObservation {
                node.id,
                { node.row, node.column },
                node.exists,
                node.type,
                node.display_name,
                std::max(node.confidence, node.existence_confidence),
                node.existence_source,
                visual_rect(node, result.normalized_bgr.size()),
                std::nullopt,
                false,
                std::nullopt,
            });
    }
    observation.edges.reserve(result.edge_detection.edges.size());
    for (const auto& edge : result.edge_detection.edges) {
        observation.edges.emplace_back(
            PerceptionEdgeObservation {
                edge.node_a,
                edge.node_b,
                edge.connected,
                edge.cnn_connected,
                edge.forced_by_connectivity_constraint,
                edge.calibrated_probability,
                edge.decision_source,
            });
    }

    BlackFlowObservationAdapter adapter;
    std::string normalization_error;
    auto normalized = adapter.normalize(observation, &normalization_error);
    if (!normalized.has_value()) {
        set_error(error, "route sandbox map normalization failed: " + normalization_error);
        return false;
    }

    NormalizedMap normalized_map;
    if (!normalized_map.merge(normalized->map, &normalization_error) ||
        !normalized_map.snapshot().validate(&normalization_error)) {
        set_error(error, "route sandbox normalized map is invalid: " + normalization_error);
        return false;
    }

    SandboxMapSnapshot next;
    next.snapshot_id = observation.observation_id;
    next.floor = *floor;
    next.action_points = observation.hud_action_points;
    next.current_node = normalized->current_node;
    next.roi = map_roi;
    next.map = normalized_map.snapshot();
    next.nodes.reserve(result.node_detection.nodes.size());
    for (const perception::Node& perceived : result.node_detection.nodes) {
        if (!perceived.exists) {
            continue;
        }
        const auto stable_id = make_stable_node_id(*floor, { perceived.row, perceived.column });
        if (!stable_id.has_value()) {
            set_error(error, "route sandbox recognized a node outside the stable grid id range");
            return false;
        }
        const Node* node = next.map.find_node(*stable_id);
        if (node == nullptr) {
            set_error(error, "route sandbox UI node is absent from the normalized map");
            return false;
        }
        next.nodes.emplace_back(
            SandboxMapNode {
                *stable_id,
                perceived.row,
                perceived.column,
                static_cast<double>(perceived.visual_center.x - map_roi.x),
                static_cast<double>(perceived.visual_center.y - map_roi.y),
                std::string(to_string(node->type)),
                node->name,
                *stable_id == normalized->current_node,
            });
    }
    std::ranges::sort(next.nodes, [](const SandboxMapNode& left, const SandboxMapNode& right) {
        return std::tie(left.row, left.column) < std::tie(right.row, right.column);
    });

    snapshot = std::move(next);
    return true;
}

bool BlackFlowSandboxObservationService::prepare(std::string* error)
{
    if (m_analyzer != nullptr) {
        return true;
    }
    m_initialization_error.clear();
    try {
        m_analyzer = BlackFlowMapPerception.acquire(m_initialization_error);
        if (m_analyzer == nullptr) {
            if (m_initialization_error.empty()) {
                m_initialization_error = "BlackFlow map perception is unavailable";
            }
            set_error(error, m_initialization_error);
            return false;
        }
        return true;
    }
    catch (const std::exception& exception) {
        m_initialization_error =
            "BlackFlow map perception initialization failed: " + std::string(exception.what());
    }
    catch (...) {
        m_initialization_error = "BlackFlow map perception initialization failed: unknown exception";
    }
    set_error(error, m_initialization_error);
    return false;
}

std::optional<int> BlackFlowSandboxObservationService::recognize_floor(const cv::Mat& image, std::string* error)
{
    const auto task = Task.get<OcrTaskInfo>(FloorTask);
    if (task == nullptr) {
        set_error(error, "route sandbox floor OCR task is missing");
        return std::nullopt;
    }
    OCRer analyzer(image);
    analyzer.set_task_info(task);
    const auto results = analyzer.analyze();
    if (!results.has_value()) {
        set_error(error, "route sandbox could not recognize the current floor name");
        return std::nullopt;
    }
    for (const auto& result : *results) {
        const auto matched = std::ranges::find(task->text, result.text);
        if (matched != task->text.end()) {
            return static_cast<int>(std::distance(task->text.begin(), matched)) + 1;
        }
    }
    set_error(error, "route sandbox floor OCR returned no configured floor name");
    return std::nullopt;
}

std::optional<int> BlackFlowSandboxObservationService::recognize_action_points(const cv::Mat& image)
{
    const auto task = Task.get<OcrTaskInfo>(ActionPointsTask);
    if (task == nullptr) {
        return std::nullopt;
    }
    OCRer analyzer(image);
    analyzer.set_task_info(task);
    const auto results = analyzer.analyze();
    if (!results.has_value() || results->size() != 1) {
        return std::nullopt;
    }
    int value = 0;
    if (!utils::chars_to_number(results->front().text, value) || value < 0 || value > 64) {
        return std::nullopt;
    }
    return value;
}
} // namespace asst::blackflow