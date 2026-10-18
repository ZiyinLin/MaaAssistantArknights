// <copyright file="BlackFlowSandboxProtocol.cs" company="MaaAssistantArknights">
// Part of the MaaWpfGui project, maintained by the MaaAssistantArknights team (Maa Team)
// Copyright (C) 2021-2025 MaaAssistantArknights Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License v3.0 only as published by
// the Free Software Foundation, either version 3 of the License, or
// any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY
// </copyright>

#nullable enable
using System.Collections.Generic;
using Newtonsoft.Json;

namespace MaaWpfGui.Models.BlackFlowSandbox;

public sealed class BlackFlowSandboxSnapshot
{
    [JsonProperty("snapshot_id")]
    public string SnapshotId { get; set; } = string.Empty;

    [JsonProperty("image_base64")]
    public string ImageBase64 { get; set; } = string.Empty;

    [JsonProperty("roi")]
    public BlackFlowSandboxRoi Roi { get; set; } = new();

    [JsonProperty("roi_width")]
    public double RoiWidth { get; set; } = 1022;

    [JsonProperty("roi_height")]
    public double RoiHeight { get; set; } = 504;

    [JsonProperty("floor")]
    public int Floor { get; set; }

    [JsonProperty("action_points")]
    public int ActionPoints { get; set; }

    [JsonProperty("current_node_id")]
    public string CurrentNodeId { get; set; } = string.Empty;

    [JsonProperty("nodes")]
    public List<BlackFlowSandboxNode> Nodes { get; set; } = [];
}

public sealed class BlackFlowSandboxRoi
{
    [JsonProperty("x")]
    public int X { get; set; } = 79;

    [JsonProperty("y")]
    public int Y { get; set; } = 95;

    [JsonProperty("width")]
    public int Width { get; set; } = 1022;

    [JsonProperty("height")]
    public int Height { get; set; } = 504;
}

public sealed class BlackFlowSandboxNode
{
    [JsonProperty("node_id")]
    public string NodeId { get; set; } = string.Empty;

    [JsonProperty("id")]
    private string LegacyNodeId
    {
        set {
            if (string.IsNullOrEmpty(NodeId))
            {
                NodeId = value;
            }
        }
    }

    [JsonProperty("x")]
    public double X { get; set; }

    [JsonProperty("y")]
    public double Y { get; set; }

    [JsonProperty("center_x")]
    private double? CenterX
    {
        set {
            if (value.HasValue)
            {
                X = value.Value;
            }
        }
    }

    [JsonProperty("center_y")]
    private double? CenterY
    {
        set {
            if (value.HasValue)
            {
                Y = value.Value;
            }
        }
    }

    [JsonProperty("enterable")]
    public bool Enterable { get; set; } = true;

    [JsonProperty("type")]
    public string Type { get; set; } = string.Empty;

    [JsonProperty("label")]
    public string Label { get; set; } = string.Empty;

    [JsonProperty("name")]
    private string Name
    {
        set {
            if (string.IsNullOrEmpty(Label))
            {
                Label = value;
            }
        }
    }
}

public sealed class BlackFlowSandboxEvaluation
{
    [JsonProperty("snapshot_id")]
    public string SnapshotId { get; set; } = string.Empty;

    [JsonProperty("validated_steps")]
    public List<BlackFlowSandboxValidatedStep> ValidatedSteps { get; set; } = [];

    [JsonProperty("remaining_action_points")]
    public int RemainingActionPoints { get; set; }

    [JsonProperty("candidates")]
    public List<BlackFlowSandboxCandidate> Candidates { get; set; } = [];
}

public sealed class BlackFlowSandboxValidatedStep
{
    [JsonProperty("movement_kind")]
    public string MovementKind { get; set; } = string.Empty;

    [JsonProperty("target_node_id")]
    public string TargetNodeId { get; set; } = string.Empty;

    [JsonProperty("landing_node_id")]
    public string LandingNodeId { get; set; } = string.Empty;

    [JsonProperty("assumed_landing_node_id")]
    public string AssumedLandingNodeId { get; set; } = string.Empty;

    [JsonProperty("path_node_ids")]
    public List<string> PathNodeIds { get; set; } = [];

    [JsonProperty("action_point_cost")]
    public int ActionPointCost { get; set; }

    [JsonProperty("remaining_action_points")]
    public int RemainingActionPoints { get; set; }

    [JsonProperty("valid")]
    public bool IsValid { get; set; } = true;

    [JsonProperty("error")]
    public string Error { get; set; } = string.Empty;

    [JsonProperty("predicted")]
    public bool Predicted { get; set; }
}

public sealed class BlackFlowSandboxCandidate
{
    [JsonProperty("movement_kind")]
    public string MovementKind { get; set; } = string.Empty;

    [JsonProperty("target_node_id")]
    public string TargetNodeId { get; set; } = string.Empty;

    [JsonProperty("node_id")]
    private string NodeId
    {
        set {
            if (string.IsNullOrEmpty(TargetNodeId))
            {
                TargetNodeId = value;
            }
        }
    }

    [JsonProperty("landing_node_id")]
    public string LandingNodeId { get; set; } = string.Empty;

    [JsonProperty("path_node_ids")]
    public List<string> PathNodeIds { get; set; } = [];

    [JsonProperty("possible_landing_node_ids")]
    public List<string> PossibleLandingNodeIds { get; set; } = [];

    [JsonProperty("action_point_cost")]
    public int ActionPointCost { get; set; }

    [JsonProperty("controllable")]
    public bool Controllable { get; set; } = true;

    [JsonProperty("uses_inferred_edge")]
    public bool UsesInferredEdge { get; set; }

    [JsonProperty("predicted")]
    public bool Predicted { get; set; }
}
