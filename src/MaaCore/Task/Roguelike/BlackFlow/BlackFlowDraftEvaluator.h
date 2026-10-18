#pragma once

#include <cstddef>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

#include "BlackFlowModel.h"

namespace asst::blackflow
{
struct DraftStep
{
    MovementKind movement = MovementKind::Walk;
    NodeId target = InvalidNodeId;
    std::optional<NodeId> assumed_landing;
};

struct ValidatedDraftStep
{
    bool valid = false;
    std::string error;
    MovementKind movement = MovementKind::Walk;
    NodeId source = InvalidNodeId;
    NodeId target = InvalidNodeId;
    NodeId landing = InvalidNodeId;
    std::vector<NodeId> path;
    int action_point_cost = 0;
    int action_point_gain = 0;
    int remaining_action_points = 0;
    bool uncertain_landing = false;
    bool uses_inferred_edge = false;
};

struct DraftCandidate
{
    MovementKind movement = MovementKind::Walk;
    NodeId source = InvalidNodeId;
    NodeId target = InvalidNodeId;
    NodeId landing = InvalidNodeId;
    std::vector<NodeId> path;
    std::vector<NodeId> possible_landings;
    int action_point_cost = 0;
    int action_point_gain = 0;
    bool controllable = true;
    bool uncertain_landing = false;
    bool uses_unconfirmed_edge = false;
    bool uses_inferred_edge = false;
};

struct DraftEvaluation
{
    std::vector<ValidatedDraftStep> steps;
    RunState final_state;
    std::vector<DraftCandidate> candidates;
    std::optional<std::size_t> invalid_step_index;
};

class BlackFlowDraftEvaluator
{
public:
    [[nodiscard]] bool evaluate(
        const MapSnapshot& map,
        RunState initial_state,
        const std::vector<DraftStep>& steps,
        std::optional<MovementKind> next_movement,
        GraphLayer graph_layer,
        DraftEvaluation& evaluation,
        std::string* error = nullptr) const;
};

[[nodiscard]] std::optional<MovementKind> movement_kind_from_code(std::string_view value) noexcept;
[[nodiscard]] std::string_view movement_kind_code(MovementKind movement) noexcept;
} // namespace asst::blackflow