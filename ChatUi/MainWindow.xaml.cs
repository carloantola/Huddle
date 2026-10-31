using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using ChatProtocols;
using ChatUi.ViewModels;

namespace ChatUi;

public partial class MainWindow : Window
{
    private readonly MainViewModel viewModel;
    private bool closeAfterDisconnect;
    private bool disconnectingForClose;
    private BoardTool boardTool = BoardTool.Select;
    private readonly List<BoardPoint> drawingPoints = [];
    private bool isDrawing;
    private BoardHitTarget? selectedTarget;
    private BoardHitTarget? dragTarget;
    private Point dragStart;
    private Vector dragOffset;
    private bool isDragging;
    private TextBox? textEditor;
    private string? editingTextId;
    private Point textEditorPosition;
    private bool finishingTextEdit;

    public MainWindow()
    {
        InitializeComponent();
        var chatSession = new ChatSession(new ChatClientFactory(), new ChatServerFactory());
        viewModel = new MainViewModel(Dispatcher, chatSession);
        DataContext = viewModel;
        viewModel.Board.BoardChanged += RenderBoard;
        RenderBoard();
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        await viewModel.ConnectAsync();
    }

    private async void Host_Click(object sender, RoutedEventArgs e)
    {
        await viewModel.HostAsync();
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        await viewModel.DisconnectAsync();
    }

    private async void RefreshRooms_Click(object sender, RoutedEventArgs e)
    {
        await viewModel.RefreshRoomsAsync();
    }

    private async void RefreshUsers_Click(object sender, RoutedEventArgs e)
    {
        await viewModel.RefreshUsersAsync();
    }

    private async void JoinRoom_Click(object sender, RoutedEventArgs e)
    {
        await viewModel.JoinSelectedRoomAsync();
    }

    private async void LeaveRoom_Click(object sender, RoutedEventArgs e)
    {
        await viewModel.LeaveCurrentRoomAsync();
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        await viewModel.SendChatAsync();
    }

    private void SelectBoardTool_Click(object sender, RoutedEventArgs e)
    {
        boardTool = BoardTool.Select;
    }

    private void DrawBoardTool_Click(object sender, RoutedEventArgs e)
    {
        boardTool = BoardTool.Draw;
    }

    private void TextBoardTool_Click(object sender, RoutedEventArgs e)
    {
        boardTool = BoardTool.Text;
    }

    private async void SyncBoard_Click(object sender, RoutedEventArgs e)
    {
        await viewModel.Board.SwitchRoomAsync(viewModel.CurrentRoom);
    }

    private async void DeleteBoardObject_Click(object sender, RoutedEventArgs e)
    {
        await DeleteSelectedBoardObjectAsync();
    }

    private void BoardCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!viewModel.IsConnected || !viewModel.Board.IsReady)
        {
            return;
        }

        if (textEditor is not null)
        {
            BoardCanvas.Focus();
            return;
        }

        BoardCanvas.Focus();
        var position = e.GetPosition(BoardCanvas);
        if (boardTool == BoardTool.Draw)
        {
            isDrawing = true;
            drawingPoints.Clear();
            drawingPoints.Add(ToBoardPoint(position));
            RenderBoard();
            BoardCanvas.CaptureMouse();
            e.Handled = true;
            return;
        }

        if (boardTool == BoardTool.Text)
        {
            BeginTextEdit(position, null);
            e.Handled = true;
            return;
        }

        selectedTarget = FindBoardTarget(position);
        RenderBoard();
        if (e.ClickCount == 2 && selectedTarget is { IsText: true })
        {
            var text = viewModel.Board.GetTexts()
                .FirstOrDefault(item => item.Id == selectedTarget.Id);
            if (text is not null)
            {
                BeginTextEdit(
                    new Point(text.Position.X, text.Position.Y),
                    text);
            }

            e.Handled = true;
            return;
        }

        if (selectedTarget is not null)
        {
            dragTarget = selectedTarget;
            dragStart = position;
            dragOffset = default;
            isDragging = true;
            BoardCanvas.CaptureMouse();
        }

        e.Handled = true;
    }

    private void BoardCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (isDrawing)
        {
            var position = e.GetPosition(BoardCanvas);
            var last = drawingPoints[^1];
            var deltaX = position.X - last.X;
            var deltaY = position.Y - last.Y;
            if (deltaX * deltaX + deltaY * deltaY >= 2.25)
            {
                drawingPoints.Add(ToBoardPoint(position));
                RenderBoard();
            }

            return;
        }

        if (isDragging)
        {
            var position = e.GetPosition(BoardCanvas);
            dragOffset = position - dragStart;
            RenderBoard();
        }
    }

    private async void BoardCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (isDrawing)
        {
            isDrawing = false;
            BoardCanvas.ReleaseMouseCapture();
            var points = drawingPoints
                .Select(point => new BoardPoint { X = point.X, Y = point.Y })
                .ToList();
            drawingPoints.Clear();
            RenderBoard();

            if (points.Count > 0)
            {
                await viewModel.Board.AddStrokeAsync(new BoardStroke
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Points = points,
                    Width = 3,
                    Color = "#1E293B"
                });
            }

            e.Handled = true;
            return;
        }

        if (!isDragging)
        {
            return;
        }

        isDragging = false;
        BoardCanvas.ReleaseMouseCapture();
        var target = dragTarget;
        var offset = dragOffset;
        dragTarget = null;
        dragOffset = default;
        RenderBoard();

        if (target is not null && offset.LengthSquared >= 4)
        {
            await viewModel.Board.MoveObjectAsync(new MoveBoardObjectPayload
            {
                Id = target.Id,
                Offset = new BoardPoint { X = (float)offset.X, Y = (float)offset.Y }
            });
        }

        e.Handled = true;
    }

    private async void BoardCanvas_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && textEditor is null)
        {
            e.Handled = true;
            await DeleteSelectedBoardObjectAsync();
        }
        else if (e.Key == Key.Escape && textEditor is not null)
        {
            e.Handled = true;
            CancelTextEdit();
        }
    }

    private async Task DeleteSelectedBoardObjectAsync()
    {
        if (selectedTarget is null || !viewModel.Board.IsReady)
        {
            return;
        }

        var target = selectedTarget;
        selectedTarget = null;
        await viewModel.Board.DeleteObjectAsync(new DeleteBoardObjectPayload { Id = target.Id });
        RenderBoard();
    }

    private async void BeginTextEdit(Point position, BoardText? existing)
    {
        if (textEditor is not null)
        {
            await FinishTextEditAsync(commit: true);
        }

        editingTextId = existing?.Id;
        textEditorPosition = position;
        textEditor = new TextBox
        {
            Text = existing?.Text ?? "",
            Width = 220,
            MinHeight = 32,
            MaxHeight = 120,
            AcceptsReturn = false,
            Background = Brushes.White,
            Foreground = GetBrush(existing?.Color ?? "#111827"),
            BorderBrush = Brushes.DodgerBlue,
            BorderThickness = new Thickness(1),
            FontSize = existing?.FontSize ?? 18,
            Padding = new Thickness(4),
            ToolTip = "Type text and press Enter to save; Escape cancels."
        };
        textEditor.KeyDown += TextEditor_KeyDown;
        textEditor.LostKeyboardFocus += TextEditor_LostKeyboardFocus;
        Canvas.SetLeft(textEditor, position.X);
        Canvas.SetTop(textEditor, position.Y);
        Panel.SetZIndex(textEditor, 1000);
        BoardCanvas.Children.Add(textEditor);
        textEditor.Focus();
        textEditor.SelectAll();
    }

    private async void TextEditor_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && textEditor is not null)
        {
            e.Handled = true;
            await FinishTextEditAsync(commit: true);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelTextEdit();
        }
    }

    private async void TextEditor_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        await FinishTextEditAsync(commit: true);
    }

    private async Task FinishTextEditAsync(bool commit)
    {
        if (textEditor is null || finishingTextEdit)
        {
            return;
        }

        finishingTextEdit = true;
        var editor = textEditor;
        var text = editor.Text.Trim();
        editor.KeyDown -= TextEditor_KeyDown;
        editor.LostKeyboardFocus -= TextEditor_LostKeyboardFocus;
        BoardCanvas.Children.Remove(editor);
        textEditor = null;

        if (commit && text.Length > 0)
        {
            if (editingTextId is null)
            {
                await viewModel.Board.AddTextAsync(new BoardText
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Text = text,
                    Position = ToBoardPoint(textEditorPosition),
                    FontSize = 18,
                    Color = "#111827"
                });
            }
            else
            {
                await viewModel.Board.EditTextAsync(new EditBoardTextPayload
                {
                    Id = editingTextId,
                    Text = text
                });
            }
        }

        editingTextId = null;
        finishingTextEdit = false;
        RenderBoard();
    }

    private void CancelTextEdit()
    {
        if (textEditor is null)
        {
            return;
        }

        _ = FinishTextEditAsync(commit: false);
    }

    private BoardHitTarget? FindBoardTarget(Point position)
    {
        var element = BoardCanvas.InputHitTest(position) as DependencyObject;
        while (element is not null)
        {
            if (element is FrameworkElement frameworkElement &&
                frameworkElement.Tag is BoardHitTarget target)
            {
                return target;
            }

            element = VisualTreeHelper.GetParent(element);
        }

        return null;
    }

    private void RenderBoard()
    {
        if (BoardCanvas is null)
        {
            return;
        }

        for (var index = BoardCanvas.Children.Count - 1; index >= 0; index--)
        {
            if (!ReferenceEquals(BoardCanvas.Children[index], textEditor))
            {
                BoardCanvas.Children.RemoveAt(index);
            }
        }

        foreach (var stroke in viewModel.Board.GetStrokes())
        {
            var isSelected = selectedTarget?.Id == stroke.Id;
            var offset = isDragging && dragTarget?.Id == stroke.Id ? dragOffset : default;
            var points = new PointCollection(stroke.Points.Select(
                point => new Point(point.X + offset.X, point.Y + offset.Y)));
            if (points.Count == 1)
            {
                var radius = Math.Max(1.5, stroke.Width / 2);
                var dot = new Ellipse
                {
                    Width = radius * 2,
                    Height = radius * 2,
                    Fill = isSelected ? Brushes.DodgerBlue : GetBrush(stroke.Color),
                    Tag = new BoardHitTarget(stroke.Id, false)
                };
                Canvas.SetLeft(dot, points[0].X - radius);
                Canvas.SetTop(dot, points[0].Y - radius);
                BoardCanvas.Children.Add(dot);
                continue;
            }

            BoardCanvas.Children.Add(new Polyline
            {
                Points = points,
                Stroke = isSelected ? Brushes.DodgerBlue : GetBrush(stroke.Color),
                StrokeThickness = Math.Max(1, stroke.Width + (isSelected ? 1 : 0)),
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Tag = new BoardHitTarget(stroke.Id, false)
            });
        }

        foreach (var text in viewModel.Board.GetTexts())
        {
            var offset = isDragging && dragTarget?.Id == text.Id ? dragOffset : default;
            var textBlock = new TextBlock
            {
                Text = text.Text,
                FontSize = text.FontSize,
                Foreground = selectedTarget?.Id == text.Id
                    ? Brushes.DodgerBlue
                    : GetBrush(text.Color),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 350
            };

            FrameworkElement item = textBlock;
            var left = text.Position.X + offset.X;
            var top = text.Position.Y + offset.Y;
            if (selectedTarget?.Id == text.Id)
            {
                item = new Border
                {
                    BorderBrush = Brushes.DodgerBlue,
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(2),
                    Child = textBlock,
                    Tag = new BoardHitTarget(text.Id, true)
                };
                left -= 2;
                top -= 2;
            }
            else
            {
                item.Tag = new BoardHitTarget(text.Id, true);
            }

            Canvas.SetLeft(item, left);
            Canvas.SetTop(item, top);
            BoardCanvas.Children.Add(item);
        }

        if (isDrawing && drawingPoints.Count > 0)
        {
            var points = new PointCollection(drawingPoints.Select(
                point => new Point(point.X, point.Y)));
            if (points.Count == 1)
            {
                var point = points[0];
                var dot = new Ellipse
                {
                    Width = 3,
                    Height = 3,
                    Fill = Brushes.DarkSlateGray,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(dot, point.X - 1.5);
                Canvas.SetTop(dot, point.Y - 1.5);
                BoardCanvas.Children.Add(dot);
            }
            else
            {
                var previewPolyline = new Polyline
                {
                    Points = points,
                    Stroke = Brushes.DarkSlateGray,
                    StrokeThickness = 3,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round,
                    IsHitTestVisible = false
                };
                BoardCanvas.Children.Add(previewPolyline);
            }
        }
    }

    private static BoardPoint ToBoardPoint(Point point) =>
        new() { X = (float)point.X, Y = (float)point.Y };

    private static Brush GetBrush(string color)
    {
        try
        {
            return (Brush)new BrushConverter().ConvertFromString(color)!;
        }
        catch (FormatException)
        {
            return Brushes.Black;
        }
    }

    private enum BoardTool
    {
        Select,
        Draw,
        Text
    }

    private sealed record BoardHitTarget(string Id, bool IsText);

    private async void MessageInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await viewModel.SendChatAsync();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (closeAfterDisconnect)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        if (disconnectingForClose)
        {
            return;
        }

        disconnectingForClose = true;
        _ = DisconnectThenCloseAsync();
    }

    private async Task DisconnectThenCloseAsync()
    {
        try
        {
            await viewModel.DisconnectAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"Error while closing the chat client: {ex}");
        }
        finally
        {
            closeAfterDisconnect = true;
            _ = Dispatcher.InvokeAsync(Close, DispatcherPriority.Background);
        }
    }
}
