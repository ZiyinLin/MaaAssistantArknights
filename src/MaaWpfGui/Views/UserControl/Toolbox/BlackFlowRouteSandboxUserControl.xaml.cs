// <copyright file="BlackFlowRouteSandboxUserControl.xaml.cs" company="MaaAssistantArknights">
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

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MaaWpfGui.ViewModels.UserControl.Toolbox;

namespace MaaWpfGui.Views.UserControl.Toolbox;

/// <summary>
/// Interaction logic for BlackFlowRouteSandboxUserControl.xaml.
/// </summary>
public partial class BlackFlowRouteSandboxUserControl : System.Windows.Controls.UserControl
{
    private const double MinimumMapScale = 1;
    private const double MaximumMapScale = 4;
    private const double MapScaleStep = 1.15;

    private Point? _mapDragStart;
    private Matrix _mapDragStartMatrix = Matrix.Identity;
    private bool _isMapDragging;
    private double _mapScale = MinimumMapScale;

    public BlackFlowRouteSandboxUserControl()
    {
        InitializeComponent();
    }

    private void NodeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: BlackFlowSandboxNodeViewModel node } element ||
            DataContext is not BlackFlowRouteSandboxUserControlModel viewModel)
        {
            return;
        }

        if (!viewModel.SelectMovementAfterNode)
        {
            viewModel.SelectNode(node);
            return;
        }

        var choices = viewModel.GetNodeMovementChoices(node);
        if (choices.Count == 0)
        {
            return;
        }

        var menu = new ContextMenu {
            Placement = PlacementMode.MousePoint,
            PlacementTarget = element,
        };
        foreach (var choice in choices)
        {
            var item = new MenuItem {
                Header = viewModel.GetMovementChoiceDisplay(choice),
            };
            item.Click += (_, _) => viewModel.SelectNode(node, choice);
            menu.Items.Add(item);
        }

        element.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private void MapViewport_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var requestedScale = e.Delta > 0
            ? _mapScale * MapScaleStep
            : _mapScale / MapScaleStep;
        var nextScale = Math.Clamp(requestedScale, MinimumMapScale, MaximumMapScale);
        if (Math.Abs(nextScale - _mapScale) < 0.001)
        {
            e.Handled = true;
            return;
        }

        if (Math.Abs(nextScale - MinimumMapScale) < 0.001)
        {
            ResetMapTransform();
            e.Handled = true;
            return;
        }

        var position = e.GetPosition(MapViewbox);
        var matrix = MapTransform.Matrix;
        var scaleFactor = nextScale / _mapScale;
        matrix.ScaleAtPrepend(scaleFactor, scaleFactor, position.X, position.Y);
        MapTransform.Matrix = matrix;
        _mapScale = nextScale;
        e.Handled = true;
    }

    private void MapViewport_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _mapDragStart = e.GetPosition(MapViewport);
        _mapDragStartMatrix = MapTransform.Matrix;
        _isMapDragging = false;
    }

    private void MapViewport_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_mapDragStart.HasValue || e.LeftButton != MouseButtonState.Pressed ||
            _mapScale <= MinimumMapScale)
        {
            return;
        }

        var position = e.GetPosition(MapViewport);
        var delta = position - _mapDragStart.Value;
        if (!_isMapDragging)
        {
            if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            _isMapDragging = MapViewport.CaptureMouse();
            if (!_isMapDragging)
            {
                return;
            }
            MapViewport.Cursor = Cursors.SizeAll;
        }

        var matrix = _mapDragStartMatrix;
        matrix.Translate(delta.X, delta.Y);
        MapTransform.Matrix = matrix;
        e.Handled = true;
    }

    private void MapViewport_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isMapDragging)
        {
            e.Handled = true;
            MapViewport.ReleaseMouseCapture();
        }

        EndMapDrag();
    }

    private void MapViewport_LostMouseCapture(object sender, MouseEventArgs e)
    {
        EndMapDrag();
    }

    private void UserControl_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            ResetMapTransform();
        }
        else
        {
            EndMapDrag();
        }
    }

    private void ResetMapTransform()
    {
        MapTransform.Matrix = Matrix.Identity;
        _mapScale = MinimumMapScale;
        EndMapDrag();
    }

    private void EndMapDrag()
    {
        if (MapViewport.IsMouseCaptured)
        {
            MapViewport.ReleaseMouseCapture();
        }

        _mapDragStart = null;
        _isMapDragging = false;
        MapViewport.Cursor = Cursors.Arrow;
    }
}