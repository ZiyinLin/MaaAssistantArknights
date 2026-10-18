#include "BlackFlowDraftEvaluator.h"

#include <algorithm>
#include <ranges>
#include <utility>

namespace asst::blackflow
{
namespace
{
void set_error(std::string* error, std::string message)
{
    if (error != nullptr) {
        *error = std::move(message);
    }
}

bool contains_landing(const MoveCandidate& move, NodeId landing)
{
    return std::ranges::find(move.possible_landings, landing) != move.possible_landings.end();
}

bool project_move(
    const MapSnapshot& map,
    const RunState& current,
    const MoveCandidate& move,
    std::optional<NodeId> assumed_landing,
    RunState& next,
    ValidatedDraftStep& validated,
    std::string& error)
{
    if (move.source != current.current_node || move.predicted_action_point_cost < 0 ||
        move.predicted_action_point_cost > current.resources.action_points) {
        error = "move is not affordable from the current draft state";
        return false;
    }

    const MovementSpec* movement = find_movement_spec(move.movement);
    if (movement == nullptr) {
        error = "move references an unknown movement";
        return false;
    }

    NodeId target = move.target;
    NodeId landing = move.landing;
    int action_point_gain = move.predicted_action_point_gain;
    bool uncertain_landing = false;
    if (!move.controllable) {
        if (!assumed_landing.has_value() || !contains_landing(move, *assumed_landing)) {
            error = "random movement requires one of its possible landings to be selected";
            return false;
        }
        target = *assumed_landing;
        landing = *assumed_landing;
        uncertain_landing = true;
        if (const auto gain = move.landing_action_point_gains.find(landing);
            gain != move.landing_action_point_gains.end()) {
            action_point_gain = gain->second;
        }
    }
    else if (assumed_landing.has_value() && *assumed_landing != landing) {
        error = "selected landing does not match the deterministic movement result";
        return false;
    }

    const Node* target_node = map.find_node(target);
    const Node* landing_node = map.find_node(landing);
    if (target_node == nullptr || landing_node == nullptr || target_node->floor != current.floor ||
        landing_node->floor != current.floor) {
        error = "move target or landing is absent from the current floor map";
        return false;
    }

    next = current;
    next.current_node = landing;
    next.resources.action_points = action_points_after(
        current.resources.action_points,
        move.predicted_action_point_cost,
        action_point_gain);
    next.resources.hope += movement->effect.hope_gain;
    next.resources.ingots += movement->effect.ingot_gain;

    if (move.movement != MovementKind::Walk) {
        auto charge = next.resources.movement_charges.find(move.movement);
        if (charge != next.resources.movement_charges.end()) {
            if (charge->second <= 0) {
                error = "selected movement has no remaining manually entered charge";
                return false;
            }
            --charge->second;
        }
    }

    next.visited_nodes.emplace(target);
    if (!target_node->traversal.repeatable && target_node->type != NodeType::Empty) {
        next.node_progress.insert_or_assign(target, NodeProgress::Completed);
    }
    if (target_node->type == NodeType::Light && !next.consumed_one_time_nodes.contains(target)) {
        next.consumed_one_time_nodes.emplace(target);
        const auto revealed = map.nodes_within_manhattan(target, 2);
        next.revealed_nodes.insert(revealed.begin(), revealed.end());
    }
    if (move.movement != MovementKind::Walk && is_combat_node_type(target_node->type) &&
        next.resources.white_model_birds > 0) {
        --next.resources.white_model_birds;
    }
    if (next.resources != current.resources) {
        ++next.resources_revision;
    }
    next.strategy_terminal = move.terminal_on_completion || is_exit_node_type(target_node->type);

    validated.valid = true;
    validated.movement = move.movement;
    validated.source = current.current_node;
    validated.target = target;
    validated.landing = landing;
    validated.path = move.path;
    validated.action_point_cost = move.predicted_action_point_cost;
    validated.action_point_gain = action_point_gain;
    validated.remaining_action_points = next.resources.action_points;
    validated.uncertain_landing = uncertain_landing;
    validated.uses_inferred_edge = move.uses_inferred_edge;
    return true;
}

DraftCandidate make_candidate(const MoveCandidate& move, NodeId target, NodeId landing, int action_point_gain)
{
    DraftCandidate result;
    result.movement = move.movement;
    result.source = move.source;
    result.target = target;
    result.landing = landing;
    result.path = move.path;
    result.possible_landings = move.possible_landings;
    result.action_point_cost = move.predicted_action_point_cost;
    result.action_point_gain = action_point_gain;
    result.controllable = move.controllable;
    result.uncertain_landing = !move.controllable;
    result.uses_unconfirmed_edge = move.uses_unconfirmed_edge;
    result.uses_inferred_edge = move.uses_inferred_edge;
    return result;
}
} // namespace

bool BlackFlowDraftEvaluator::evaluate(
    const MapSnapshot& map,
    RunState initial_state,
    const std::vector<DraftStep>& steps,
    std::optional<MovementKind> next_movement,
    GraphLayer graph_layer,
    DraftEvaluation& evaluation,
    std::string* error) const
{
    std::string validation_error;
    if (!map.validate(&validation_error)) {
        set_error(error, "draft evaluation received an invalid map: " + validation_error);
        return false;
    }
    const Node* initial_node = map.find_node(initial_state.current_node);
    if (initial_node == nullptr || initial_node->floor != initial_state.floor ||
        initial_state.resources.action_points < 0) {
        set_error(error, "draft evaluation requires a valid current node, floor, and action point value");
        return false;
    }
    if (!initial_state.costs.validate(&validation_error)) {
        set_error(error, "draft evaluation received an invalid cost model: " + validation_error);
        return false;
    }

    evaluation = {};
    evaluation.final_state = std::move(initial_state);
    evaluation.final_state.visited_nodes.emplace(evaluation.final_state.current_node);
    if (!initial_node->traversal.repeatable && initial_node->type != NodeType::Empty) {
        evaluation.final_state.node_progress.insert_or_assign(initial_node->id, NodeProgress::Completed);
    }
    if (initial_node->type == NodeType::Light) {
        evaluation.final_state.consumed_one_time_nodes.emplace(initial_node->id);
        const auto revealed = map.nodes_within_manhattan(initial_node->id, 2);
        evaluation.final_state.revealed_nodes.insert(revealed.begin(), revealed.end());
    }
    evaluation.steps.reserve(steps.size());

    for (std::size_t index = 0; index < steps.size(); ++index) {
        const DraftStep& step = steps[index];
        ValidatedDraftStep validated;
        validated.movement = step.movement;
        validated.source = evaluation.final_state.current_node;
        validated.target = step.target;
        validated.landing = step.assumed_landing.value_or(InvalidNodeId);
        validated.remaining_action_points = evaluation.final_state.resources.action_points;

        if (evaluation.final_state.strategy_terminal) {
            validated.error = "a terminal node cannot be followed by another draft step";
            evaluation.invalid_step_index = index;
            evaluation.steps.emplace_back(std::move(validated));
            return true;
        }

        const auto actions = enumerate_move_actions(map, evaluation.final_state, graph_layer, true, true);
        const MoveCandidate* selected = nullptr;
        std::optional<NodeId> selected_landing = step.assumed_landing;
        for (const MoveAction& action : actions) {
            const MoveCandidate& candidate = action.candidate;
            if (candidate.movement != step.movement) {
                continue;
            }
            if (candidate.controllable && candidate.target == step.target) {
                selected = &candidate;
                break;
            }
            if (!candidate.controllable) {
                const NodeId requested_landing = step.assumed_landing.value_or(step.target);
                if (requested_landing != InvalidNodeId && contains_landing(candidate, requested_landing)) {
                    selected = &candidate;
                    selected_landing = requested_landing;
                    break;
                }
            }
        }

        if (selected == nullptr) {
            validated.error = "step is not reachable with the selected movement and current draft state";
            evaluation.invalid_step_index = index;
            evaluation.steps.emplace_back(std::move(validated));
            return true;
        }

        RunState next;
        std::string projection_error;
        if (!project_move(
                map,
                evaluation.final_state,
                *selected,
                selected_landing,
                next,
                validated,
                projection_error)) {
            validated.error = std::move(projection_error);
            evaluation.invalid_step_index = index;
            evaluation.steps.emplace_back(std::move(validated));
            return true;
        }
        evaluation.final_state = std::move(next);
        evaluation.steps.emplace_back(std::move(validated));
    }

    if (evaluation.final_state.strategy_terminal) {
        return true;
    }

    const auto actions = enumerate_move_actions(map, evaluation.final_state, graph_layer, true, true);
    for (const MoveAction& action : actions) {
        const MoveCandidate& move = action.candidate;
        if (next_movement.has_value() && move.movement != *next_movement) {
            continue;
        }
        if (move.predicted_action_point_cost < 0 ||
            move.predicted_action_point_cost > evaluation.final_state.resources.action_points) {
            continue;
        }
        if (!move.controllable) {
            for (const NodeId landing : move.possible_landings) {
                int gain = move.predicted_action_point_gain;
                if (const auto found = move.landing_action_point_gains.find(landing);
                    found != move.landing_action_point_gains.end()) {
                    gain = found->second;
                }
                DraftCandidate candidate = make_candidate(move, landing, landing, gain);
                candidate.possible_landings = { landing };
                evaluation.candidates.emplace_back(std::move(candidate));
            }
            continue;
        }
        evaluation.candidates.emplace_back(
            make_candidate(move, move.target, move.landing, move.predicted_action_point_gain));
    }

    std::ranges::sort(evaluation.candidates, [](const DraftCandidate& left, const DraftCandidate& right) {
        if (left.target != right.target) {
            return left.target < right.target;
        }
        return movement_kind_code(left.movement) < movement_kind_code(right.movement);
    });
    return true;
}

std::optional<MovementKind> movement_kind_from_code(std::string_view value) noexcept
{
    for (const MovementSpec& movement : movement_specs()) {
        if (value == movement_kind_code(movement.kind) || value == movement.id) {
            return movement.kind;
        }
    }
    return std::nullopt;
}

std::string_view movement_kind_code(MovementKind movement) noexcept
{
    switch (movement) {
    case MovementKind::Walk:
        return "Walk";
    case MovementKind::M01:
        return "M01";
    case MovementKind::M02:
        return "M02";
    case MovementKind::M03:
        return "M03";
    case MovementKind::M04:
        return "M04";
    case MovementKind::M05:
        return "M05";
    case MovementKind::M06:
        return "M06";
    case MovementKind::M07:
        return "M07";
    case MovementKind::M08:
        return "M08";
    case MovementKind::M09:
        return "M09";
    case MovementKind::M10:
        return "M10";
    case MovementKind::M11:
        return "M11";
    case MovementKind::M12:
        return "M12";
    }
    return "Unknown";
}
} // namespace asst::blackflow