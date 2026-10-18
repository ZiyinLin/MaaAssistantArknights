#pragma once

#include <cstdint>
#include <memory>
#include <optional>
#include <string>
#include <vector>

#include <opencv2/core.hpp>

#include "BlackFlowModel.h"
#include "Vision/Roguelike/BlackFlow/BlackFlowMapAnalyzer.h"

namespace asst::blackflow
{
struct SandboxMapNode
{
    NodeId id = InvalidNodeId;
    int row = 0;
    int column = 0;
    double x = 0.0;
    double y = 0.0;
    std::string type;
    std::string name;
    bool current = false;
};

struct SandboxMapSnapshot
{
    std::string snapshot_id;
    int floor = 0;
    std::optional<int> action_points;
    NodeId current_node = InvalidNodeId;
    cv::Rect roi;
    MapSnapshot map;
    std::vector<SandboxMapNode> nodes;
};

class BlackFlowSandboxObservationService
{
public:
    [[nodiscard]] bool recognize(
        const cv::Mat& image,
        SandboxMapSnapshot& snapshot,
        std::string* error = nullptr);

private:
    [[nodiscard]] bool prepare(std::string* error);
    [[nodiscard]] static std::optional<int> recognize_floor(const cv::Mat& image, std::string* error);
    [[nodiscard]] static std::optional<int> recognize_action_points(const cv::Mat& image);

    std::shared_ptr<const perception::BlackFlowMapAnalyzer> m_analyzer;
    std::string m_initialization_error;
    std::uint64_t m_sequence = 0;
};
} // namespace asst::blackflow