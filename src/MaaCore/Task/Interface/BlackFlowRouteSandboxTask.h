#pragma once

#include <condition_variable>
#include <deque>
#include <mutex>
#include <optional>
#include <string>
#include <string_view>

#include "Task/InterfaceTask.h"
#include "Task/Roguelike/BlackFlow/BlackFlowDraftEvaluator.h"
#include "Task/Roguelike/BlackFlow/BlackFlowSandboxObservationService.h"

namespace asst
{
class BlackFlowRouteSandboxTask final : public InterfaceTask
{
public:
    inline static constexpr std::string_view TaskType = "BlackFlowRouteSandbox";

    BlackFlowRouteSandboxTask(const AsstCallback& callback, Assistant* inst);
    ~BlackFlowRouteSandboxTask() override = default;

    bool run() override;
    bool set_params(const json::value& params) override;

protected:
    bool _run() override;

private:
    void handle_refresh(const json::value& command, const std::string& request_id);
    void handle_evaluate(const json::value& command, const std::string& request_id);
    void emit_error(const std::string& request_id, std::string message);
    void emit_snapshot(
        const std::string& request_id,
        const blackflow::SandboxMapSnapshot& snapshot,
        const std::string& image_base64);
    void emit_evaluation(
        const std::string& request_id,
        const std::string& snapshot_id,
        const blackflow::DraftEvaluation& evaluation);

    std::mutex m_command_mutex;
    std::condition_variable m_command_ready;
    std::deque<json::value> m_commands;
    blackflow::BlackFlowSandboxObservationService m_observation_service;
    blackflow::BlackFlowDraftEvaluator m_evaluator;
    std::optional<blackflow::SandboxMapSnapshot> m_snapshot;
};
} // namespace asst