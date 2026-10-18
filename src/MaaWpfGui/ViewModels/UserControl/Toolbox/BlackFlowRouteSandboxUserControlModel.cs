// <copyright file="BlackFlowRouteSandboxUserControlModel.cs" company="MaaAssistantArknights">
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
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MaaWpfGui.Helper;
using MaaWpfGui.Main;
using MaaWpfGui.Models;
using MaaWpfGui.Models.BlackFlowSandbox;
using MaaWpfGui.ViewModels.UI;
using Newtonsoft.Json.Linq;
using Stylet;

namespace MaaWpfGui.ViewModels.UserControl.Toolbox;

public sealed class BlackFlowRouteSandboxUserControlModel : PropertyChangedBase
{
    private const string CallbackWhat = "BlackFlowRouteSandbox";
    private const string AutomaticMovementKind = "Automatic";
    private readonly ToolboxViewModel _owner;
    private readonly Dictionary<string, int> _movementCharges = new(StringComparer.Ordinal);
    private readonly HashSet<string> _movementChargeConstraints = new(StringComparer.Ordinal);
    private long _requestSequence;
    private int _taskId;
    private string _latestRequestId = string.Empty;
    private string _mapScopeKey = string.Empty;
    private bool _snapshotIsCurrent;
    private bool _evaluationIsCurrent;
    private bool _closeRequested;

    public BlackFlowRouteSandboxUserControlModel(ToolboxViewModel owner)
    {
        _owner = owner;
        for (var movementIndex = 1; movementIndex <= 12; ++movementIndex)
        {
            _movementCharges[$"M{movementIndex:00}"] = 0;
        }

        RebuildMovementOptions();
        var initialPlan = new BlackFlowRoutePlan("A");
        Plans.Add(initialPlan);
        _selectedPlan = initialPlan;
        RebuildVisibleSteps();

        Instances.AsstProxy.AsstTaskChainMsgEvent += OnAsstTaskChainMsg;
        Instances.AsstProxy.AsstSubTaskMsgEvent += OnAsstSubTaskMsg;
        LocalizationHelper.LanguageChanged += () => Execute.OnUIThread(() => {
            RebuildMovementOptions();
            RebuildVisibleSteps();
            NotifyOfPropertyChange(nameof(MapStatus));
        });
    }

    public ObservableCollection<BlackFlowRoutePlan> Plans { get; } = [];

    public ObservableCollection<BlackFlowSandboxMovementChoice> MovementOptions { get; } = [];

    public ObservableCollection<BlackFlowSandboxNodeViewModel> Nodes { get; } = [];

    public ObservableCollection<BlackFlowSandboxRouteSegmentViewModel> RouteSegments { get; } = [];

    public ObservableCollection<BlackFlowRouteStepViewModel> VisibleSteps { get; } = [];

    private BlackFlowRoutePlan _selectedPlan;

    public BlackFlowRoutePlan SelectedPlan
    {
        get => _selectedPlan;
        set {
            if (value == null || !SetAndNotify(ref _selectedPlan, value))
            {
                return;
            }

            ClearCandidateHighlights();
            RebuildVisibleSteps();
            RefreshMapPresentation();
            NotifyHistoryChanged();
            RequestEvaluation();
        }
    }

    private BlackFlowRouteStepViewModel? _selectedStep;

    public BlackFlowRouteStepViewModel? SelectedStep
    {
        get => _selectedStep;
        set {
            if (SetAndNotify(ref _selectedStep, value))
            {
                NotifyOfPropertyChange(nameof(CanBranch));
                RefreshMapPresentation();
            }
        }
    }

    private BlackFlowSandboxMovementChoice? _selectedMovement;

    public BlackFlowSandboxMovementChoice? SelectedMovement
    {
        get => _selectedMovement;
        set {
            if (value == null || !SetAndNotify(ref _selectedMovement, value))
            {
                return;
            }

            NotifyOfPropertyChange(nameof(CanEditMovementCharges));
            _selectedMovementCharges = value.Kind is "Walk" or AutomaticMovementKind
                ? 0
                : _movementCharges.GetValueOrDefault(value.Kind);
            NotifyOfPropertyChange(nameof(SelectedMovementCharges));
            ClearCandidateHighlights();
            RequestEvaluation();
        }
    }

    private int _selectedMovementCharges;

    public int SelectedMovementCharges
    {
        get => _selectedMovementCharges;
        set {
            if (!SetAndNotify(ref _selectedMovementCharges, value) || SelectedMovement == null ||
                SelectedMovement.Kind is "Walk" or AutomaticMovementKind)
            {
                return;
            }

            _movementCharges[SelectedMovement.Kind] = value;
            if (value > 0)
            {
                _movementChargeConstraints.Add(SelectedMovement.Kind);
            }
            else
            {
                _movementChargeConstraints.Remove(SelectedMovement.Kind);
            }
            ClearCandidateHighlights();
            RequestEvaluation();
        }
    }

    public bool CanEditMovementCharges => SelectedMovement != null &&
        SelectedMovement.Kind is not "Walk" and not AutomaticMovementKind;

    private System.Windows.Media.Imaging.BitmapSource? _mapImage;

    public System.Windows.Media.Imaging.BitmapSource? MapImage
    {
        get => _mapImage;
        private set => SetAndNotify(ref _mapImage, value);
    }

    private double _roiWidth = 1022;

    public double RoiWidth
    {
        get => _roiWidth;
        private set => SetAndNotify(ref _roiWidth, value);
    }

    private double _roiHeight = 504;

    public double RoiHeight
    {
        get => _roiHeight;
        private set => SetAndNotify(ref _roiHeight, value);
    }

    private int _floor;

    public int Floor
    {
        get => _floor;
        private set {
            if (SetAndNotify(ref _floor, value))
            {
                NotifyOfPropertyChange(nameof(MapStatus));
            }
        }
    }

    private int _actionPoints;

    public int ActionPoints
    {
        get => _actionPoints;
        private set {
            if (SetAndNotify(ref _actionPoints, value))
            {
                NotifyOfPropertyChange(nameof(MapStatus));
            }
        }
    }

    private int _remainingActionPoints;

    public int RemainingActionPoints
    {
        get => _remainingActionPoints;
        private set => SetAndNotify(ref _remainingActionPoints, value);
    }

    private int _reachableNodeCount;

    public int ReachableNodeCount
    {
        get => _reachableNodeCount;
        private set => SetAndNotify(ref _reachableNodeCount, value);
    }

    private string _currentSnapshotId = string.Empty;

    public string CurrentSnapshotId
    {
        get => _currentSnapshotId;
        private set => SetAndNotify(ref _currentSnapshotId, value);
    }

    private string _currentNodeId = string.Empty;

    public string CurrentNodeId
    {
        get => _currentNodeId;
        private set => SetAndNotify(ref _currentNodeId, value);
    }

    private string _errorMessage = string.Empty;

    public string ErrorMessage
    {
        get => _errorMessage;
        private set {
            if (SetAndNotify(ref _errorMessage, value))
            {
                NotifyOfPropertyChange(nameof(ErrorVisibility));
            }
        }
    }

    public Visibility ErrorVisibility => string.IsNullOrEmpty(ErrorMessage) ? Visibility.Collapsed : Visibility.Visible;

    private bool _isBusy;

    public bool IsBusy
    {
        get => _isBusy;
        private set {
            if (SetAndNotify(ref _isBusy, value))
            {
                NotifyOfPropertyChange(nameof(CanInteract));
            }
        }
    }

    public bool HasSnapshot => !string.IsNullOrEmpty(CurrentSnapshotId) && MapImage != null;

    public bool CanInteract => HasSnapshot && _snapshotIsCurrent && !IsBusy;

    public string MapStatus => string.Format(
        LocalizationHelper.GetString("BlackFlowRouteSandboxMapStatus"),
        Floor,
        ActionPoints);

    public bool CanUndo => SelectedPlan.OwnSteps.Count > 0;

    public bool CanRedo => SelectedPlan.RedoSteps.Count > 0;

    public bool CanBranch => SelectedStep != null;

    public bool CanDeletePlan => Plans.Count > 1;

    public bool SelectMovementAfterNode => SelectedMovement?.Kind == AutomaticMovementKind;

    public bool Open()
    {
        ErrorMessage = string.Empty;
        ResetDraftState(clearMap: true);
        _closeRequested = false;
        IsBusy = true;
        var request = CreateCommand("open", includeDraft: false);
        var (success, taskId) = Instances.AsstProxy.AsstAppendBlackFlowRouteSandbox(request);
        if (!success)
        {
            IsBusy = false;
            ErrorMessage = LocalizationHelper.GetString("BlackFlowRouteSandboxStartFailed");
            return false;
        }

        _taskId = taskId;
        if (!Instances.AsstProxy.AsstStart())
        {
            var closeCommand = CreateCommand("close", includeDraft: false);
            Instances.AsstProxy.AsstSetBlackFlowRouteSandboxParams(_taskId, closeCommand);
            _taskId = 0;
            IsBusy = false;
            ErrorMessage = LocalizationHelper.GetString("BlackFlowRouteSandboxStartFailed");
            return false;
        }

        return true;
    }

    public void ReturnToMiniGame()
    {
        if (_taskId <= 0)
        {
            _owner.ExitBlackFlowRouteSandbox();
            return;
        }

        ErrorMessage = string.Empty;
        SetSnapshotCurrent(false);
        IsBusy = true;
        _closeRequested = true;
        var command = CreateCommand("close", includeDraft: false);
        if (!Instances.AsstProxy.AsstSetBlackFlowRouteSandboxParams(_taskId, command))
        {
            _closeRequested = false;
            IsBusy = false;
            ErrorMessage = LocalizationHelper.GetString("BlackFlowRouteSandboxCommandFailed");
        }
    }

    public void RefreshMap()
    {
        if (_taskId <= 0)
        {
            return;
        }

        ErrorMessage = string.Empty;
        ClearCandidateHighlights();
        SetSnapshotCurrent(false);
        IsBusy = true;
        var command = CreateCommand("refresh", includeDraft: false);
        if (!Instances.AsstProxy.AsstSetBlackFlowRouteSandboxParams(_taskId, command))
        {
            IsBusy = false;
            ErrorMessage = LocalizationHelper.GetString("BlackFlowRouteSandboxCommandFailed");
        }
    }

    public void AddPlan()
    {
        var plan = new BlackFlowRoutePlan(FindFirstAvailablePlanName());
        Plans.Add(plan);
        NotifyOfPropertyChange(nameof(CanDeletePlan));
        SelectedPlan = plan;
    }

    public void DeletePlan()
    {
        if (!CanDeletePlan)
        {
            return;
        }

        var result = MessageBoxHelper.Show(
            string.Format(LocalizationHelper.GetString("BlackFlowRouteSandboxDeletePlanConfirm"), SelectedPlan.Name),
            LocalizationHelper.GetString("BlackFlowRouteSandbox"),
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.OK)
        {
            return;
        }

        var selectedIndex = Plans.IndexOf(SelectedPlan);
        Plans.Remove(SelectedPlan);
        NotifyOfPropertyChange(nameof(CanDeletePlan));
        SelectedPlan = Plans[selectedIndex > 0 ? selectedIndex - 1 : 0];
    }

    public void BranchFromSelectedStep()
    {
        if (SelectedStep == null)
        {
            return;
        }

        var sharedPrefix = SelectedPlan.Steps.Take(SelectedStep.Index + 1).ToArray();
        var branch = new BlackFlowRoutePlan(FindFirstAvailablePlanName(), sharedPrefix);
        Plans.Add(branch);
        NotifyOfPropertyChange(nameof(CanDeletePlan));
        SelectedPlan = branch;
    }

    public void Undo()
    {
        if (!CanUndo)
        {
            return;
        }

        var step = SelectedPlan.OwnSteps[^1];
        SelectedPlan.OwnSteps.RemoveAt(SelectedPlan.OwnSteps.Count - 1);
        SelectedPlan.RedoSteps.Push(step);
        RebuildAfterPlanEdit();
        NotifyHistoryChanged();
    }

    public void Redo()
    {
        if (!CanRedo)
        {
            return;
        }

        var step = SelectedPlan.RedoSteps.Pop();
        SelectedPlan.OwnSteps.Add(step);
        RebuildAfterPlanEdit(step.StepId);
        NotifyHistoryChanged();
    }

    public IReadOnlyList<BlackFlowSandboxCandidateSelection> GetNodeMovementChoices(BlackFlowSandboxNodeViewModel node)
    {
        return node.Candidates
            .OrderBy(candidate => GetMovementOrder(candidate.MovementKind))
            .ToArray();
    }

    public string GetMovementChoiceDisplay(BlackFlowSandboxCandidateSelection candidate)
    {
        var charges = candidate.MovementKind == "Walk" ||
            !_movementChargeConstraints.Contains(candidate.MovementKind)
            ? string.Empty
            : $" · ×{_movementCharges.GetValueOrDefault(candidate.MovementKind)}";
        var randomness = candidate.Controllable
            ? string.Empty
            : $" · {LocalizationHelper.GetString("SupportUnitUsage.Random")}";
        return $"{GetMovementDisplay(candidate.MovementKind)}{charges}{randomness} · -{candidate.ActionPointCost} AP";
    }

    public void SelectNode(BlackFlowSandboxNodeViewModel node)
    {
        if (SelectedMovement == null || SelectMovementAfterNode)
        {
            return;
        }

        var candidate = node.Candidates.FirstOrDefault(value => value.MovementKind == SelectedMovement.Kind);
        if (candidate != null)
        {
            SelectNode(node, candidate);
        }
    }

    public void SelectNode(
        BlackFlowSandboxNodeViewModel node,
        BlackFlowSandboxCandidateSelection candidate)
    {
        if (!node.Candidates.Contains(candidate))
        {
            return;
        }

        var step = new BlackFlowRouteStep(
            candidate.MovementKind,
            candidate.TargetNodeId,
            candidate.AssumedLandingNodeId);
        SelectedPlan.OwnSteps.Add(step);
        SelectedPlan.RedoSteps.Clear();
        RebuildAfterPlanEdit(step.StepId);
        NotifyHistoryChanged();
    }
    private void OnAsstTaskChainMsg(AsstMsg type, int taskId)
    {
        if (taskId != _taskId)
        {
            return;
        }
        if (type is AsstMsg.TaskChainError or AsstMsg.TaskChainCompleted or AsstMsg.TaskChainStopped)
        {
            HandleTaskEnded();
        }
    }

    private void OnAsstSubTaskMsg(AsstMsg type, AsstSubTaskMsg? message)
    {
        if (message == null || message.TaskId != _taskId)
        {
            return;
        }
        if (type != AsstMsg.SubTaskExtraInfo)
        {
            if (type is AsstMsg.SubTaskError or AsstMsg.SubTaskCompleted or AsstMsg.SubTaskStopped)
            {
                HandleTaskEnded();
            }
            return;
        }
        if (message.What != CallbackWhat || message.Details == null)
        {
            return;
        }

        var details = message.Details;
        var requestId = details.Value<string>("request_id") ?? string.Empty;
        if (requestId != _latestRequestId)
        {
            return;
        }

        IsBusy = false;
        if (details.Value<bool?>("ok") == false ||
            string.Equals(details.Value<string>("status"), "error", StringComparison.OrdinalIgnoreCase))
        {
            ErrorMessage = ReadError(details["error"]);
            return;
        }
        if (details.Value<bool?>("closed") == true)
        {
            _taskId = 0;
            _closeRequested = false;
            SetSnapshotCurrent(false);
            _owner.ExitBlackFlowRouteSandbox();
            return;
        }

        ErrorMessage = string.Empty;
        var snapshot = details["snapshot"]?.ToObject<BlackFlowSandboxSnapshot>();
        var evaluation = details["evaluation"]?.ToObject<BlackFlowSandboxEvaluation>();
        var snapshotApplied = snapshot != null && ApplySnapshot(snapshot);

        if (evaluation != null)
        {
            ApplyEvaluation(evaluation);
        }
        else if (snapshotApplied)
        {
            RequestEvaluation();
        }
    }

    private bool ApplySnapshot(BlackFlowSandboxSnapshot snapshot)
    {
        if (string.IsNullOrEmpty(snapshot.SnapshotId))
        {
            ErrorMessage = LocalizationHelper.GetString("BlackFlowRouteSandboxInvalidSnapshot");
            return false;
        }

        System.Windows.Media.Imaging.BitmapSource image;
        try
        {
            if (!string.IsNullOrEmpty(snapshot.ImageBase64))
            {
                var encodedImage = snapshot.ImageBase64;
                var separatorIndex = encodedImage.IndexOf(',');
                if (encodedImage.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && separatorIndex >= 0)
                {
                    encodedImage = encodedImage[(separatorIndex + 1)..];
                }

                var bytes = Convert.FromBase64String(encodedImage);
                using var stream = new MemoryStream(bytes, writable: false);
                var decodedImage = new BitmapImage();
                decodedImage.BeginInit();
                decodedImage.CacheOption = BitmapCacheOption.OnLoad;
                decodedImage.StreamSource = stream;
                decodedImage.EndInit();
                decodedImage.Freeze();
                image = decodedImage;
            }
            else
            {
                var fullFrame = Instances.AsstProxy.AsstGetImage();
                if (fullFrame == null)
                {
                    throw new InvalidOperationException("The cached frame is unavailable.");
                }

                var roi = snapshot.Roi;
                var roiRect = new Int32Rect(roi.X, roi.Y, roi.Width, roi.Height);
                if (roiRect.X < 0 || roiRect.Y < 0 || roiRect.Width <= 0 || roiRect.Height <= 0 ||
                    roiRect.X + roiRect.Width > fullFrame.PixelWidth || roiRect.Y + roiRect.Height > fullFrame.PixelHeight)
                {
                    throw new InvalidOperationException("The sandbox ROI is outside the cached frame.");
                }

                var croppedImage = new CroppedBitmap(fullFrame, roiRect);
                croppedImage.Freeze();
                image = croppedImage;
            }
        }
        catch (Exception)
        {
            ErrorMessage = LocalizationHelper.GetString("BlackFlowRouteSandboxInvalidImage");
            return false;
        }

        var nextMapScopeKey = BuildMapScopeKey(snapshot);
        if (!string.IsNullOrEmpty(_mapScopeKey) && !string.Equals(_mapScopeKey, nextMapScopeKey, StringComparison.Ordinal))
        {
            ResetDraftState(clearMap: false);
        }
        _mapScopeKey = nextMapScopeKey;

        MapImage = image;
        CurrentSnapshotId = snapshot.SnapshotId;
        RoiWidth = snapshot.Roi.Width > 0 ? snapshot.Roi.Width : (snapshot.RoiWidth > 0 ? snapshot.RoiWidth : 1022);
        RoiHeight = snapshot.Roi.Height > 0 ? snapshot.Roi.Height : (snapshot.RoiHeight > 0 ? snapshot.RoiHeight : 504);
        Floor = snapshot.Floor;
        ActionPoints = snapshot.ActionPoints;
        RemainingActionPoints = snapshot.ActionPoints;
        CurrentNodeId = snapshot.CurrentNodeId;

        Nodes.Clear();
        foreach (var node in snapshot.Nodes.Where(node => !string.IsNullOrEmpty(node.NodeId)))
        {
            Nodes.Add(new BlackFlowSandboxNodeViewModel(node, node.NodeId == CurrentNodeId));
        }

        _evaluationIsCurrent = false;
        ResetStepEvaluations();
        ClearCandidateHighlights();
        RefreshMapPresentation();
        NotifyOfPropertyChange(nameof(HasSnapshot));
        SetSnapshotCurrent(true);
        return true;
    }

    private void ApplyEvaluation(BlackFlowSandboxEvaluation evaluation)
    {
        if (evaluation.SnapshotId != CurrentSnapshotId)
        {
            return;
        }

        ResetStepEvaluations();
        var routeSteps = SelectedPlan.Steps;
        for (var index = 0; index < routeSteps.Count; ++index)
        {
            var step = routeSteps[index];
            if (index >= evaluation.ValidatedSteps.Count)
            {
                step.IsValid = false;
                step.Error = LocalizationHelper.GetString("BlackFlowRouteSandboxStepNotValidated");
                continue;
            }

            var validated = evaluation.ValidatedSteps[index];
            step.LandingNodeId = validated.LandingNodeId;
            step.PathNodeIds = validated.PathNodeIds;
            step.ActionPointCost = validated.ActionPointCost;
            step.RemainingActionPoints = validated.RemainingActionPoints;
            step.IsValid = validated.IsValid;
            step.IsPredicted = validated.Predicted;
            step.Error = validated.Error;
        }

        _evaluationIsCurrent = true;
        RemainingActionPoints = evaluation.RemainingActionPoints;
        ApplyCandidateHighlights(evaluation.Candidates);
        RebuildVisibleSteps(SelectedStep?.Step.StepId);
        RefreshMapPresentation();
    }

    private void ApplyCandidateHighlights(IEnumerable<BlackFlowSandboxCandidate> candidates)
    {
        ClearCandidateHighlights();
        var candidatesByNode = new Dictionary<string, List<BlackFlowSandboxCandidateSelection>>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            if (!candidate.Controllable && candidate.PossibleLandingNodeIds.Count > 0)
            {
                foreach (var landingNodeId in candidate.PossibleLandingNodeIds)
                {
                    AddCandidateSelection(
                        landingNodeId,
                        new BlackFlowSandboxCandidateSelection(
                            candidate.MovementKind,
                            string.IsNullOrEmpty(candidate.TargetNodeId) ? landingNodeId : candidate.TargetNodeId,
                            landingNodeId,
                            candidate.ActionPointCost,
                            candidate.Predicted,
                            candidate.UsesInferredEdge,
                            controllable: false));
                }

                continue;
            }

            AddCandidateSelection(
                candidate.TargetNodeId,
                new BlackFlowSandboxCandidateSelection(
                    candidate.MovementKind,
                    candidate.TargetNodeId,
                    string.Empty,
                    candidate.ActionPointCost,
                    candidate.Predicted,
                    candidate.UsesInferredEdge,
                    candidate.Controllable));
        }

        foreach (var node in Nodes)
        {
            if (candidatesByNode.TryGetValue(node.NodeId, out var nodeCandidates))
            {
                node.SetCandidates(nodeCandidates);
            }
        }

        ReachableNodeCount = Nodes.Count(node => node.IsCandidate);

        void AddCandidateSelection(string nodeId, BlackFlowSandboxCandidateSelection selection)
        {
            if (string.IsNullOrEmpty(nodeId))
            {
                return;
            }
            if (!candidatesByNode.TryGetValue(nodeId, out var selections))
            {
                selections = [];
                candidatesByNode.Add(nodeId, selections);
            }
            selections.Add(selection);
        }
    }
    private void ClearCandidateHighlights()
    {
        foreach (var node in Nodes)
        {
            node.ClearCandidates();
        }

        ReachableNodeCount = 0;
    }

    private void RefreshMapPresentation()
    {
        foreach (var node in Nodes)
        {
            node.ClearStepBadges();
            node.IsPlanned = false;
            node.IsSelectedStepTarget = false;
        }

        RouteSegments.Clear();
        if (!_evaluationIsCurrent)
        {
            return;
        }

        var nodesById = Nodes.ToDictionary(node => node.NodeId, StringComparer.Ordinal);
        nodesById.TryGetValue(CurrentNodeId, out var previousNode);
        var steps = SelectedPlan.Steps;
        for (var index = 0; index < steps.Count; ++index)
        {
            var step = steps[index];
            if (!step.IsValid)
            {
                break;
            }
            var isSelectedStep = SelectedStep?.Step.StepId == step.StepId;
            if (!nodesById.TryGetValue(step.EffectiveLandingNodeId, out var landingNode))
            {
                continue;
            }

            landingNode.IsPlanned = true;
            var badge = $"{SelectedPlan.Name}{index + 1}";
            landingNode.AddStepBadge(badge, isSelectedStep);
            if (isSelectedStep)
            {
                landingNode.IsSelectedStepTarget = true;
            }

            var pathNodes = step.PathNodeIds
                .Select(nodeId => nodesById.GetValueOrDefault(nodeId))
                .Where(node => node != null)
                .Cast<BlackFlowSandboxNodeViewModel>()
                .ToArray();
            if (pathNodes.Length == 0)
            {
                pathNodes = [landingNode];
            }

            foreach (var pathNode in pathNodes)
            {
                if (previousNode != null && previousNode.NodeId != pathNode.NodeId)
                {
                    RouteSegments.Add(new BlackFlowSandboxRouteSegmentViewModel(
                        previousNode.CenterX,
                        previousNode.CenterY,
                        pathNode.CenterX,
                        pathNode.CenterY,
                        step.MovementKind,
                        step.IsPredicted,
                        isSelectedStep,
                        RoiWidth,
                        RoiHeight));
                }
                previousNode = pathNode;
            }

            if (previousNode == null || previousNode.NodeId != landingNode.NodeId)
            {
                if (previousNode != null)
                {
                    RouteSegments.Add(new BlackFlowSandboxRouteSegmentViewModel(
                        previousNode.CenterX,
                        previousNode.CenterY,
                        landingNode.CenterX,
                        landingNode.CenterY,
                        step.MovementKind,
                        step.IsPredicted,
                        isSelectedStep,
                        RoiWidth,
                        RoiHeight));
                }
                previousNode = landingNode;
            }
        }
    }
    private void RebuildAfterPlanEdit(string selectedStepId = "")
    {
        ClearCandidateHighlights();
        RebuildVisibleSteps(selectedStepId);
        RefreshMapPresentation();
        RequestEvaluation();
    }

    private void RebuildVisibleSteps(string? selectedStepId = null)
    {
        selectedStepId ??= SelectedStep?.Step.StepId;
        VisibleSteps.Clear();
        var steps = SelectedPlan.Steps;
        for (var index = 0; index < steps.Count; ++index)
        {
            var step = steps[index];
            var targetDisplay = Nodes.FirstOrDefault(node => node.NodeId == step.TargetNodeId)?.Label ?? step.TargetNodeId;
            VisibleSteps.Add(new BlackFlowRouteStepViewModel(
                step,
                index,
                $"{SelectedPlan.Name}{index + 1}",
                GetMovementDisplay(step.MovementKind),
                targetDisplay));
        }

        SelectedStep = VisibleSteps.FirstOrDefault(step => step.Step.StepId == selectedStepId)
            ?? VisibleSteps.LastOrDefault();
    }

    private void RequestEvaluation()
    {
        if (_taskId <= 0 || string.IsNullOrEmpty(CurrentSnapshotId) || SelectedMovement == null)
        {
            return;
        }

        _evaluationIsCurrent = false;
        ResetStepEvaluations();
        RefreshMapPresentation();
        IsBusy = true;
        var command = CreateCommand("evaluate", includeDraft: true);
        if (!Instances.AsstProxy.AsstSetBlackFlowRouteSandboxParams(_taskId, command))
        {
            IsBusy = false;
            ErrorMessage = LocalizationHelper.GetString("BlackFlowRouteSandboxCommandFailed");
        }
    }

    private JObject CreateCommand(string command, bool includeDraft)
    {
        var requestId = $"wpf-{++_requestSequence}";
        _latestRequestId = requestId;
        var result = new JObject {
            ["command"] = command,
            ["request_id"] = requestId,
        };

        if (!includeDraft)
        {
            return result;
        }

        result["snapshot_id"] = CurrentSnapshotId;
        if (SelectedMovement?.Kind != AutomaticMovementKind)
        {
            result["movement_kind"] = SelectedMovement?.Kind ?? "Walk";
        }
        result["movement_charges"] = JObject.FromObject(_movementCharges
            .Where(charge => _movementChargeConstraints.Contains(charge.Key))
            .ToDictionary(charge => charge.Key, charge => charge.Value));
        result["include_inferred_edges"] = true;
        result["steps"] = new JArray(SelectedPlan.Steps.Select(step => {
            var serialized = new JObject {
                ["movement_kind"] = step.MovementKind,
                ["target_node_id"] = SerializeNodeId(step.TargetNodeId),
            };
            if (!string.IsNullOrEmpty(step.AssumedLandingNodeId))
            {
                serialized["assumed_landing_node_id"] = SerializeNodeId(step.AssumedLandingNodeId);
            }

            return serialized;
        }));
        return result;
    }

    private static JToken SerializeNodeId(string nodeId)
    {
        return new JValue(nodeId);
    }

    private static string BuildMapScopeKey(BlackFlowSandboxSnapshot snapshot)
    {
        return $"floor:{snapshot.Floor}";
    }

    private void ResetDraftState(bool clearMap)
    {
        var initialPlan = new BlackFlowRoutePlan("A");
        Plans.Clear();
        Plans.Add(initialPlan);
        _selectedPlan = initialPlan;
        NotifyOfPropertyChange(nameof(SelectedPlan));
        SelectedStep = null;
        VisibleSteps.Clear();
        NotifyOfPropertyChange(nameof(CanDeletePlan));
        NotifyHistoryChanged();

        _movementChargeConstraints.Clear();
        foreach (var movement in _movementCharges.Keys.ToArray())
        {
            _movementCharges[movement] = 0;
        }
        _selectedMovementCharges = 0;
        NotifyOfPropertyChange(nameof(SelectedMovementCharges));

        _mapScopeKey = string.Empty;
        _evaluationIsCurrent = false;
        SetSnapshotCurrent(false);
        Nodes.Clear();
        RouteSegments.Clear();
        ReachableNodeCount = 0;
        RemainingActionPoints = 0;
        if (clearMap)
        {
            MapImage = null;
            CurrentSnapshotId = string.Empty;
            CurrentNodeId = string.Empty;
            Floor = 0;
            ActionPoints = 0;
            NotifyOfPropertyChange(nameof(HasSnapshot));
        }
        RebuildVisibleSteps();
    }

    private void ResetStepEvaluations()
    {
        foreach (var step in Plans.SelectMany(plan => plan.Steps).Distinct())
        {
            step.LandingNodeId = string.Empty;
            step.PathNodeIds = Array.Empty<string>();
            step.ActionPointCost = 0;
            step.RemainingActionPoints = 0;
            step.IsValid = false;
            step.IsPredicted = false;
            step.Error = LocalizationHelper.GetString("BlackFlowRouteSandboxStepNotValidated");
        }
    }

    private void HandleTaskEnded()
    {
        var returnToMiniGame = _closeRequested;
        _taskId = 0;
        _closeRequested = false;
        IsBusy = false;
        SetSnapshotCurrent(false);
        if (returnToMiniGame)
        {
            _owner.ExitBlackFlowRouteSandbox();
            return;
        }
        ErrorMessage = LocalizationHelper.GetString("BlackFlowRouteSandboxCommandFailed");
    }

    private void RebuildMovementOptions()
    {
        var selectedKind = SelectedMovement?.Kind ?? "Walk";
        MovementOptions.Clear();
        MovementOptions.Add(new BlackFlowSandboxMovementChoice(
            AutomaticMovementKind,
            LocalizationHelper.GetString("BlackFlowRouteSandboxMovementAutomatic")));
        MovementOptions.Add(new BlackFlowSandboxMovementChoice("Walk", LocalizationHelper.GetString("BlackFlowRouteSandboxMovementWalk")));
        for (var movementIndex = 1; movementIndex <= 12; ++movementIndex)
        {
            var kind = $"M{movementIndex:00}";
            MovementOptions.Add(new BlackFlowSandboxMovementChoice(
                kind,
                LocalizationHelper.GetString($"BlackFlowRouteSandboxMovement{kind}")));
        }

        SelectedMovement = MovementOptions.FirstOrDefault(movement => movement.Kind == selectedKind)
            ?? MovementOptions[0];
    }

    private string GetMovementDisplay(string movementKind)
    {
        return MovementOptions.FirstOrDefault(movement => movement.Kind == movementKind)?.Display ?? movementKind;
    }

    private static int GetMovementOrder(string movementKind)
    {
        if (movementKind == "Walk")
        {
            return 0;
        }

        return movementKind.Length > 1 && movementKind[0] == 'M' && int.TryParse(movementKind[1..], out var movementIndex)
            ? movementIndex
            : int.MaxValue;
    }

    private string FindFirstAvailablePlanName()
    {
        var existingNames = Plans.Select(plan => plan.Name).ToHashSet(StringComparer.Ordinal);
        for (var index = 0; ; ++index)
        {
            var name = GetAlphabeticName(index);
            if (!existingNames.Contains(name))
            {
                return name;
            }
        }
    }

    private static string GetAlphabeticName(int index)
    {
        var name = string.Empty;
        do
        {
            name = (char)('A' + (index % 26)) + name;
            index = (index / 26) - 1;
        }
        while (index >= 0);

        return name;
    }


    private void NotifyHistoryChanged()
    {
        NotifyOfPropertyChange(nameof(CanUndo));
        NotifyOfPropertyChange(nameof(CanRedo));
    }

    private void SetSnapshotCurrent(bool value)
    {
        if (_snapshotIsCurrent == value)
        {
            return;
        }

        _snapshotIsCurrent = value;
        NotifyOfPropertyChange(nameof(CanInteract));
    }
    private static string ReadError(JToken? token)
    {
        if (token == null || token.Type == JTokenType.Null)
        {
            return LocalizationHelper.GetString("BlackFlowRouteSandboxUnknownError");
        }

        if (token.Type == JTokenType.String)
        {
            return token.ToString();
        }

        return token["message"]?.ToString() ?? token.ToString();
    }

}

public sealed class BlackFlowSandboxMovementChoice
{
    public BlackFlowSandboxMovementChoice(string kind, string display)
    {
        Kind = kind;
        Display = display;
    }

    public string Kind { get; }

    public string Display { get; }
}

public sealed class BlackFlowSandboxCandidateSelection
{
    public BlackFlowSandboxCandidateSelection(
        string movementKind,
        string targetNodeId,
        string assumedLandingNodeId,
        int actionPointCost,
        bool predicted,
        bool usesInferredEdge,
        bool controllable)
    {
        MovementKind = movementKind;
        TargetNodeId = targetNodeId;
        AssumedLandingNodeId = assumedLandingNodeId;
        ActionPointCost = actionPointCost;
        Predicted = predicted;
        UsesInferredEdge = usesInferredEdge;
        Controllable = controllable;
    }

    public string MovementKind { get; }

    public string TargetNodeId { get; }

    public string AssumedLandingNodeId { get; }

    public int ActionPointCost { get; }

    public bool Predicted { get; }

    public bool UsesInferredEdge { get; }

    public bool Controllable { get; }
}

public sealed class BlackFlowSandboxNodeViewModel : PropertyChangedBase
{
    public BlackFlowSandboxNodeViewModel(BlackFlowSandboxNode node, bool isCurrent)
    {
        NodeId = node.NodeId;
        NodeType = node.Type;
        CenterX = node.X;
        CenterY = node.Y;
        CanvasLeft = node.X - 12;
        CanvasTop = node.Y - 12;
        IsCurrent = isCurrent;
        StepBadgeOffsetY = node.Type is "final" or "battle_boss" or "evacuate" ? -45 : -30;
        Label = string.IsNullOrEmpty(node.Label) ? node.NodeId : node.Label;
    }

    public string NodeId { get; }

    public string NodeType { get; }

    public string Label { get; }

    public double CenterX { get; }

    public double CenterY { get; }

    public double CanvasLeft { get; }

    public double CanvasTop { get; }

    public bool IsCurrent { get; }

    public double StepBadgeOffsetY { get; }

    private bool _isPlanned;

    public bool IsPlanned
    {
        get => _isPlanned;
        set => SetAndNotify(ref _isPlanned, value);
    }

    public ObservableCollection<BlackFlowSandboxStepBadgeViewModel> StepBadges { get; } = [];

    private bool _isSelectedStepTarget;

    public bool IsSelectedStepTarget
    {
        get => _isSelectedStepTarget;
        set {
            if (SetAndNotify(ref _isSelectedStepTarget, value))
            {
                NotifyOfPropertyChange(nameof(SelectedStepTargetVisibility));
            }
        }
    }

    public IReadOnlyList<BlackFlowSandboxCandidateSelection> Candidates { get; private set; } =
        Array.Empty<BlackFlowSandboxCandidateSelection>();

    public bool IsCandidate => Candidates.Count > 0;

    public Visibility CandidateVisibility => IsCandidate ? Visibility.Visible : Visibility.Collapsed;

    public Visibility CurrentVisibility => IsCurrent ? Visibility.Visible : Visibility.Collapsed;

    public Visibility BadgeVisibility => StepBadges.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

    public Visibility SelectedStepTargetVisibility => IsSelectedStepTarget ? Visibility.Visible : Visibility.Collapsed;

    public string ToolTip
    {
        get {
            if (!IsCandidate)
            {
                return Label;
            }

            var minimumCost = Candidates.Min(candidate => candidate.ActionPointCost);
            var predicted = Candidates.Any(candidate => candidate.Predicted) ? " *" : string.Empty;
            return $"{Label} · {minimumCost} AP{predicted}";
        }
    }

    public void AddStepBadge(string text, bool isSelected)
    {
        StepBadges.Add(new BlackFlowSandboxStepBadgeViewModel(text, isSelected));
        NotifyOfPropertyChange(nameof(BadgeVisibility));
    }

    public void ClearStepBadges()
    {
        if (StepBadges.Count == 0)
        {
            return;
        }

        StepBadges.Clear();
        NotifyOfPropertyChange(nameof(BadgeVisibility));
    }

    public void SetCandidates(IEnumerable<BlackFlowSandboxCandidateSelection> candidates)
    {
        Candidates = candidates.ToArray();
        NotifyCandidateStateChanged();
    }

    public void ClearCandidates()
    {
        if (Candidates.Count == 0)
        {
            return;
        }

        Candidates = Array.Empty<BlackFlowSandboxCandidateSelection>();
        NotifyCandidateStateChanged();
    }

    private void NotifyCandidateStateChanged()
    {
        NotifyOfPropertyChange(nameof(Candidates));
        NotifyOfPropertyChange(nameof(IsCandidate));
        NotifyOfPropertyChange(nameof(CandidateVisibility));
        NotifyOfPropertyChange(nameof(ToolTip));
    }
}

public sealed class BlackFlowSandboxStepBadgeViewModel
{
    public BlackFlowSandboxStepBadgeViewModel(string text, bool isSelected)
    {
        Text = text;
        IsSelected = isSelected;
    }

    public string Text { get; }

    public bool IsSelected { get; }
}

public sealed class BlackFlowSandboxRouteSegmentViewModel
{
    public BlackFlowSandboxRouteSegmentViewModel(
        double x1,
        double y1,
        double x2,
        double y2,
        string movementKind,
        bool predicted,
        bool isSelected,
        double canvasWidth,
        double canvasHeight)
    {
        IsLongRange = movementKind is "M07" or "M08" or "M09" or "M10" or "M11";
        Predicted = predicted;
        IsSelected = isSelected;
        Geometry = CreateGeometry(x1, y1, x2, y2, canvasWidth, canvasHeight, IsLongRange, out var arrowGeometry);
        ArrowGeometry = arrowGeometry;
    }

    public Geometry Geometry { get; }

    public Geometry ArrowGeometry { get; }

    public bool IsLongRange { get; }

    public bool Predicted { get; }

    public bool IsSelected { get; }

    public Visibility RouteVisibility => !IsLongRange || IsSelected
        ? System.Windows.Visibility.Visible
        : System.Windows.Visibility.Collapsed;

    public Visibility ArrowVisibility => RouteVisibility == System.Windows.Visibility.Visible && (Predicted || IsLongRange)
        ? System.Windows.Visibility.Visible
        : System.Windows.Visibility.Collapsed;

    private static Geometry CreateGeometry(
        double x1,
        double y1,
        double x2,
        double y2,
        double canvasWidth,
        double canvasHeight,
        bool isLongRange,
        out Geometry arrowGeometry)
    {
        var start = new System.Windows.Point(x1, y1);
        var end = new System.Windows.Point(x2, y2);
        if (!isLongRange)
        {
            arrowGeometry = CreateArrowGeometry(start + ((end - start) * 0.5), end - start);
            var line = new LineGeometry(start, end);
            line.Freeze();
            return line;
        }

        var delta = end - start;
        if (delta.Length < 1)
        {
            arrowGeometry = Geometry.Empty;
            var line = new LineGeometry(start, end);
            line.Freeze();
            return line;
        }

        var midpoint = new System.Windows.Point((x1 + x2) / 2, (y1 + y2) / 2);
        var normal = new Vector(-delta.Y / delta.Length, delta.X / delta.Length);
        var canvasCenter = new System.Windows.Point(canvasWidth / 2, canvasHeight / 2);
        var outward = midpoint - canvasCenter;
        if (Vector.Multiply(normal, outward) < 0)
        {
            normal.Negate();
        }

        var offset = Math.Clamp(delta.Length * 0.28, 36, 110);
        var control = midpoint + (normal * offset);
        control.X = Math.Clamp(control.X, 12, Math.Max(12, canvasWidth - 12));
        control.Y = Math.Clamp(control.Y, 12, Math.Max(12, canvasHeight - 12));

        const double EndpointInset = 10;
        var startDirection = control - start;
        if (startDirection.Length > EndpointInset)
        {
            startDirection.Normalize();
            start += startDirection * EndpointInset;
        }
        var endDirection = control - end;
        if (endDirection.Length > EndpointInset)
        {
            endDirection.Normalize();
            end += endDirection * EndpointInset;
        }

        const double ArrowParameter = 0.5;
        var inverseArrowParameter = 1 - ArrowParameter;
        var arrowPoint = new System.Windows.Point(
            (inverseArrowParameter * inverseArrowParameter * start.X) +
                (2 * inverseArrowParameter * ArrowParameter * control.X) +
                (ArrowParameter * ArrowParameter * end.X),
            (inverseArrowParameter * inverseArrowParameter * start.Y) +
                (2 * inverseArrowParameter * ArrowParameter * control.Y) +
                (ArrowParameter * ArrowParameter * end.Y));
        var arrowTangent = ((control - start) * (2 * inverseArrowParameter)) +
            ((end - control) * (2 * ArrowParameter));
        arrowGeometry = CreateArrowGeometry(arrowPoint, arrowTangent);

        var figure = new PathFigure { StartPoint = start };
        figure.Segments.Add(new QuadraticBezierSegment(control, end, true));
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        return geometry;
    }

    private static Geometry CreateArrowGeometry(System.Windows.Point center, Vector direction)
    {
        if (direction.Length < 1)
        {
            return Geometry.Empty;
        }

        direction.Normalize();
        var normal = new Vector(-direction.Y, direction.X);
        var tip = center + (direction * 5);
        var baseCenter = center - (direction * 4);
        var left = baseCenter + (normal * 3.5);
        var right = baseCenter - (normal * 3.5);

        var figure = new PathFigure
        {
            StartPoint = tip,
            IsClosed = true,
            IsFilled = true,
        };
        figure.Segments.Add(new LineSegment(left, true));
        figure.Segments.Add(new LineSegment(right, true));
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        return geometry;
    }
}

public sealed class BlackFlowRouteStepViewModel
{
    public BlackFlowRouteStepViewModel(
        BlackFlowRouteStep step,
        int index,
        string badge,
        string movementDisplay,
        string targetDisplay)
    {
        Step = step;
        Index = index;
        Badge = badge;
        MovementDisplay = movementDisplay;
        TargetDisplay = targetDisplay;
    }

    public BlackFlowRouteStep Step { get; }

    public int Index { get; }

    public string Badge { get; }

    public string MovementDisplay { get; }

    public string TargetDisplay { get; }
}
