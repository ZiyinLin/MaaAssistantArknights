// <copyright file="BlackFlowRoutePlan.cs" company="MaaAssistantArknights">
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
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using Stylet;

namespace MaaWpfGui.Models.BlackFlowSandbox;

public sealed class BlackFlowRouteStep : PropertyChangedBase
{
    public BlackFlowRouteStep(string movementKind, string targetNodeId, string assumedLandingNodeId = "")
    {
        StepId = Guid.NewGuid().ToString("N");
        MovementKind = movementKind;
        TargetNodeId = targetNodeId;
        AssumedLandingNodeId = assumedLandingNodeId;
    }

    public string StepId { get; }

    public string MovementKind { get; }

    public string TargetNodeId { get; }

    public string AssumedLandingNodeId { get; }

    public string LandingNodeId { get; set => SetAndNotify(ref field, value); } = string.Empty;

    public IReadOnlyList<string> PathNodeIds { get; set => SetAndNotify(ref field, value); } = Array.Empty<string>();

    public int ActionPointCost { get; set => SetAndNotify(ref field, value); }

    public int RemainingActionPoints { get; set => SetAndNotify(ref field, value); }

    public bool IsValid { get; set => SetAndNotify(ref field, value); } = true;

    public bool IsPredicted { get; set => SetAndNotify(ref field, value); }

    public string Error { get; set => SetAndNotify(ref field, value); } = string.Empty;

    public string EffectiveLandingNodeId => !string.IsNullOrEmpty(LandingNodeId)
        ? LandingNodeId
        : (!string.IsNullOrEmpty(AssumedLandingNodeId) ? AssumedLandingNodeId : TargetNodeId);
}

public sealed class BlackFlowRoutePlan : PropertyChangedBase
{
    public BlackFlowRoutePlan(string name, IReadOnlyList<BlackFlowRouteStep>? sharedPrefix = null)
    {
        Name = name;
        SharedPrefix = sharedPrefix ?? Array.Empty<BlackFlowRouteStep>();
        OwnSteps.CollectionChanged += OwnSteps_CollectionChanged;
    }

    public string Name { get; }

    public IReadOnlyList<BlackFlowRouteStep> SharedPrefix { get; }

    public ObservableCollection<BlackFlowRouteStep> OwnSteps { get; } = [];

    public Stack<BlackFlowRouteStep> RedoSteps { get; } = new();

    public IReadOnlyList<BlackFlowRouteStep> Steps => SharedPrefix.Concat(OwnSteps).ToArray();

    public int StepCount => SharedPrefix.Count + OwnSteps.Count;

    private void OwnSteps_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        NotifyOfPropertyChange(nameof(Steps));
        NotifyOfPropertyChange(nameof(StepCount));
    }
}
