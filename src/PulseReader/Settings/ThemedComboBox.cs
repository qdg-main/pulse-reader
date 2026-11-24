using System.Collections;
using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace PulseReader.Settings;

internal sealed class ThemedComboBox : Control
{
    private readonly PickerItemCollection _items;
    private readonly Func<ThemedComboBox, Point> _cursorPositionProvider;
    private ToolStripDropDown? _activeDropDown;
    private ThemedDropDownList? _activeList;
    private object? _selectedItem;
    private bool _hovered;
    private bool _updating;
    private bool _suppressNextOpen;

    public ThemedComboBox()
        : this(static _ => Cursor.Position)
    {
    }

    internal ThemedComboBox(Func<ThemedComboBox, Point> cursorPositionProvider)
    {
        ArgumentNullException.ThrowIfNull(cursorPositionProvider);
        _cursorPositionProvider = cursorPositionProvider;
        _items = new PickerItemCollection(this);
        BackColor = PulseReaderTheme.Surface;
        ForeColor = PulseReaderTheme.Text;
        Font = new Font("Segoe UI", 9.25f);
        Height = 33;
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.ComboBox;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.Selectable
            | ControlStyles.UserPaint,
            true);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Content)]
    public PickerItemCollection Items => _items;

    [DefaultValue("")]
    public string DisplayMember { get; set; } = string.Empty;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public object? SelectedItem
    {
        get => _selectedItem;
        set
        {
            var next = value is not null && _items.Contains(value) ? value : null;
            if (Equals(_selectedItem, next))
            {
                return;
            }

            _selectedItem = next;
            Invalidate();
            SelectedItemChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? SelectionChangeCommitted;

    public event EventHandler? SelectedItemChanged;

    public void BeginUpdate() => _updating = true;

    public void EndUpdate()
    {
        _updating = false;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        var hovered = Enabled && (_hovered || Focused);
        var backgroundColor = Enabled
            ? hovered ? PulseReaderTheme.SurfaceHover : PulseReaderTheme.Surface
            : PulseReaderTheme.DisabledSurface;
        var borderColor = Enabled
            ? hovered ? PulseReaderTheme.Accent : PulseReaderTheme.Border
            : PulseReaderTheme.DisabledBorder;
        var textColor = Enabled ? PulseReaderTheme.Text : PulseReaderTheme.DisabledText;

        using var background = new SolidBrush(backgroundColor);
        using var border = new Pen(borderColor, hovered ? 1.5f : 1f);
        eventArgs.Graphics.FillRectangle(background, ClientRectangle);
        eventArgs.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

        var arrowArea = new Rectangle(Width - 28, 1, 27, Height - 2);
        eventArgs.Graphics.DrawLine(border, arrowArea.Left, 4, arrowArea.Left, Height - 5);
        TextRenderer.DrawText(
            eventArgs.Graphics,
            ItemText(SelectedItem),
            Font,
            new Rectangle(10, 1, Math.Max(1, Width - 44), Height - 2),
            textColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        using var arrow = new Pen(
            Enabled ? PulseReaderTheme.AccentBright : PulseReaderTheme.DisabledText,
            1.5f);
        var centerX = arrowArea.Left + arrowArea.Width / 2;
        var centerY = Height / 2;
        eventArgs.Graphics.DrawLine(arrow, centerX - 4, centerY - 2, centerX, centerY + 2);
        eventArgs.Graphics.DrawLine(arrow, centerX, centerY + 2, centerX + 4, centerY - 2);
    }

    protected override void OnMouseEnter(EventArgs eventArgs)
    {
        _hovered = true;
        Invalidate();
        base.OnMouseEnter(eventArgs);
    }

    protected override void OnMouseLeave(EventArgs eventArgs)
    {
        _hovered = false;
        Invalidate();
        base.OnMouseLeave(eventArgs);
    }

    protected override void OnMouseUp(MouseEventArgs eventArgs)
    {
        base.OnMouseUp(eventArgs);
        if (_suppressNextOpen && IsHandleCreated)
        {
            BeginInvoke(() => _suppressNextOpen = false);
        }
    }

    protected override void OnClick(EventArgs eventArgs)
    {
        ToggleDropDown();
        base.OnClick(eventArgs);
    }

    protected override void OnDoubleClick(EventArgs eventArgs)
    {
        ToggleDropDown();
        base.OnDoubleClick(eventArgs);
    }

    protected override void OnGotFocus(EventArgs eventArgs)
    {
        Invalidate();
        base.OnGotFocus(eventArgs);
    }

    protected override void OnLostFocus(EventArgs eventArgs)
    {
        Invalidate();
        base.OnLostFocus(eventArgs);
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Up or Keys.Down or Keys.Enter or Keys.Space
        || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs eventArgs)
    {
        if (eventArgs.KeyCode is Keys.Enter or Keys.Space or Keys.Up or Keys.Down or Keys.F4)
        {
            ToggleDropDown();
            eventArgs.Handled = true;
            eventArgs.SuppressKeyPress = true;
        }

        base.OnKeyDown(eventArgs);
    }

    internal void ToggleDropDown()
    {
        if (_suppressNextOpen)
        {
            _suppressNextOpen = false;
            return;
        }

        if (_activeDropDown is { } activeDropDown)
        {
            activeDropDown.Close(ToolStripDropDownCloseReason.CloseCalled);
            return;
        }

        OpenDropDown();
    }

    internal void OpenDropDown()
    {
        if (!Enabled || _items.Count == 0 || _activeDropDown is not null)
        {
            return;
        }

        var itemLabels = _items.Cast<object>().Select(ItemText).ToArray();
        var visibleRows = Math.Min(10, itemLabels.Length);
        var list = new ThemedDropDownList(
            itemLabels,
            visibleRows,
            _items.IndexOf(SelectedItem))
        {
            Font = Font,
            Size = new Size(
                Width - 2,
                Math.Max(ThemedDropDownList.ItemHeight, visibleRows * ThemedDropDownList.ItemHeight))
        };

        var host = new ToolStripControlHost(list)
        {
            AutoSize = false,
            BackColor = PulseReaderTheme.Surface,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            Size = list.Size
        };
        var dropDown = new ToolStripDropDown
        {
            AutoClose = true,
            AutoSize = false,
            BackColor = PulseReaderTheme.Surface,
            DropShadowEnabled = true,
            Margin = Padding.Empty,
            Padding = new Padding(1),
            Renderer = new ThemedDropDownRenderer(),
            Size = new Size(Width, list.Height + 2)
        };
        dropDown.Items.Add(host);
        _activeDropDown = dropDown;
        _activeList = list;

        list.ItemCommitted += CommitSelection;
        list.CancelRequested += () =>
            dropDown.Close(ToolStripDropDownCloseReason.Keyboard);
        dropDown.Closing += (_, eventArgs) =>
        {
            if (eventArgs.CloseReason == ToolStripDropDownCloseReason.AppClicked)
            {
                var pointer = PointToClient(_cursorPositionProvider(this));
                _suppressNextOpen = ClientRectangle.Contains(pointer);
            }
        };
        dropDown.Closed += (_, _) =>
        {
            if (ReferenceEquals(_activeDropDown, dropDown))
            {
                _activeDropDown = null;
                _activeList = null;
            }

            Invalidate();
            if (Disposing || IsDisposed)
            {
                return;
            }

            if (IsHandleCreated)
            {
                BeginInvoke(() =>
                {
                    if (!dropDown.IsDisposed)
                    {
                        dropDown.Dispose();
                    }
                });
            }
            else if (!dropDown.IsDisposed)
            {
                dropDown.Dispose();
            }
        };

        dropDown.Show(this, new Point(0, Height - 1));
        list.Focus();
    }

    internal void CommitSelection(int index)
    {
        if (_activeDropDown is null
            || _activeList is null
            || index < 0
            || index >= _items.Count)
        {
            return;
        }

        SelectedItem = _items[index];
        SelectionChangeCommitted?.Invoke(this, EventArgs.Empty);
        _activeDropDown.Close(ToolStripDropDownCloseReason.ItemClicked);
    }

    internal static bool IsListItemHighlighted(
        DrawItemState state,
        int itemIndex,
        int hoveredIndex) =>
        (state & DrawItemState.Selected) != 0 || itemIndex == hoveredIndex;

    private string ItemText(object? item)
    {
        if (item is null)
        {
            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(DisplayMember))
        {
            var property = TypeDescriptor.GetProperties(item)[DisplayMember];
            if (property?.GetValue(item) is { } value)
            {
                return Convert.ToString(value) ?? string.Empty;
            }
        }

        return Convert.ToString(item) ?? string.Empty;
    }

    internal sealed class PickerItemCollection(ThemedComboBox owner) : IList
    {
        private readonly List<object> _items = [];

        public int Count => _items.Count;

        public bool IsFixedSize => false;

        public bool IsReadOnly => false;

        public bool IsSynchronized => false;

        public object SyncRoot => this;

        public object? this[int index]
        {
            get => _items[index];
            set
            {
                ArgumentNullException.ThrowIfNull(value);
                _items[index] = value;
                owner.InvalidateUnlessUpdating();
            }
        }

        public int Add(object? value)
        {
            ArgumentNullException.ThrowIfNull(value);
            _items.Add(value);
            owner.InvalidateUnlessUpdating();
            return _items.Count - 1;
        }

        public void AddRange(object[] items)
        {
            ArgumentNullException.ThrowIfNull(items);
            _items.AddRange(items);
            owner.InvalidateUnlessUpdating();
        }

        public void Clear()
        {
            _items.Clear();
            owner._selectedItem = null;
            owner.InvalidateUnlessUpdating();
        }

        public bool Contains(object? value) => value is not null && _items.Contains(value);

        public int IndexOf(object? value) => value is null ? -1 : _items.IndexOf(value);

        public void Insert(int index, object? value)
        {
            ArgumentNullException.ThrowIfNull(value);
            _items.Insert(index, value);
            owner.InvalidateUnlessUpdating();
        }

        public void Remove(object? value)
        {
            if (value is not null && _items.Remove(value))
            {
                owner.InvalidateUnlessUpdating();
            }
        }

        public void RemoveAt(int index)
        {
            _items.RemoveAt(index);
            owner.InvalidateUnlessUpdating();
        }

        public void CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);

        public IEnumerator GetEnumerator() => _items.GetEnumerator();
    }

    private void InvalidateUnlessUpdating()
    {
        if (!_updating)
        {
            Invalidate();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _activeDropDown is { } dropDown)
        {
            _activeDropDown = null;
            _activeList = null;
            dropDown.Close();
            dropDown.Dispose();
        }

        base.Dispose(disposing);
    }

    private sealed class ThemedDropDownList : Control
    {
        internal const int ItemHeight = 27;
        private const int ScrollBarWidth = 16;
        private const int ScrollStep = 3;
        private readonly string[] _items;
        private readonly int _visibleRows;
        private int _selectedIndex;
        private int _hoveredIndex = -1;
        private int _firstVisibleIndex;
        private bool _draggingThumb;
        private int _thumbDragOffset;

        public ThemedDropDownList(string[] items, int visibleRows, int selectedIndex)
        {
            _items = items;
            _visibleRows = visibleRows;
            _selectedIndex = selectedIndex;
            BackColor = PulseReaderTheme.Surface;
            ForeColor = PulseReaderTheme.Text;
            Cursor = Cursors.Hand;
            TabStop = true;
            SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw
                | ControlStyles.Selectable
                | ControlStyles.UserPaint,
                true);
            EnsureSelectedItemVisible();
        }

        public event Action<int>? ItemCommitted;

        public event Action? CancelRequested;

        protected override bool IsInputKey(Keys keyData) =>
            keyData is Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown
                or Keys.Home or Keys.End or Keys.Enter or Keys.Escape
            || base.IsInputKey(keyData);

        protected override void OnPaint(PaintEventArgs eventArgs)
        {
            base.OnPaint(eventArgs);
            eventArgs.Graphics.Clear(PulseReaderTheme.Surface);

            var contentWidth = HasScrollBar ? Width - ScrollBarWidth : Width;
            for (var row = 0; row < _visibleRows; row++)
            {
                var itemIndex = _firstVisibleIndex + row;
                if (itemIndex >= _items.Length)
                {
                    break;
                }

                var bounds = new Rectangle(0, row * ItemHeight, contentWidth, ItemHeight);
                var state = itemIndex == _selectedIndex
                    ? DrawItemState.Selected
                    : DrawItemState.None;
                var highlighted = IsListItemHighlighted(state, itemIndex, _hoveredIndex);
                using var background = new SolidBrush(
                    highlighted ? PulseReaderTheme.SurfaceHover : PulseReaderTheme.Surface);
                eventArgs.Graphics.FillRectangle(background, bounds);
                TextRenderer.DrawText(
                    eventArgs.Graphics,
                    _items[itemIndex],
                    Font,
                    new Rectangle(bounds.X + 9, bounds.Y, Math.Max(1, bounds.Width - 13), bounds.Height),
                    highlighted ? PulseReaderTheme.AccentBright : PulseReaderTheme.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }

            if (HasScrollBar)
            {
                DrawScrollBar(eventArgs.Graphics);
            }
        }

        protected override void OnMouseDown(MouseEventArgs eventArgs)
        {
            Focus();
            if (HasScrollBar && eventArgs.X >= Width - ScrollBarWidth)
            {
                var thumb = ThumbBounds();
                if (thumb.Contains(eventArgs.Location))
                {
                    _draggingThumb = true;
                    _thumbDragOffset = eventArgs.Y - thumb.Top;
                }
                else
                {
                    SetFirstVisibleIndex(FirstVisibleIndexFromThumb(eventArgs.Y - thumb.Height / 2));
                }
            }
            else
            {
                var itemIndex = ItemIndexAt(eventArgs.Location);
                if (itemIndex >= 0)
                {
                    _selectedIndex = itemIndex;
                    Invalidate();
                    ItemCommitted?.Invoke(itemIndex);
                }
            }

            base.OnMouseDown(eventArgs);
        }

        protected override void OnMouseMove(MouseEventArgs eventArgs)
        {
            if (_draggingThumb)
            {
                SetFirstVisibleIndex(FirstVisibleIndexFromThumb(eventArgs.Y - _thumbDragOffset));
            }
            else
            {
                SetHoveredIndex(ItemIndexAt(eventArgs.Location));
            }

            base.OnMouseMove(eventArgs);
        }

        protected override void OnMouseUp(MouseEventArgs eventArgs)
        {
            _draggingThumb = false;
            Invalidate();
            base.OnMouseUp(eventArgs);
        }

        protected override void OnMouseLeave(EventArgs eventArgs)
        {
            if (!_draggingThumb)
            {
                SetHoveredIndex(-1);
            }

            base.OnMouseLeave(eventArgs);
        }

        protected override void OnMouseWheel(MouseEventArgs eventArgs)
        {
            var notches = Math.Max(1, Math.Abs(eventArgs.Delta) / SystemInformation.MouseWheelScrollDelta);
            var direction = eventArgs.Delta > 0 ? -1 : 1;
            SetFirstVisibleIndex(_firstVisibleIndex + direction * ScrollStep * notches);
            SetHoveredIndex(ItemIndexAt(eventArgs.Location));
            base.OnMouseWheel(eventArgs);
        }

        protected override void OnKeyDown(KeyEventArgs eventArgs)
        {
            var nextIndex = _selectedIndex;
            switch (eventArgs.KeyCode)
            {
                case Keys.Up:
                    nextIndex--;
                    break;
                case Keys.Down:
                    nextIndex++;
                    break;
                case Keys.PageUp:
                    nextIndex -= _visibleRows;
                    break;
                case Keys.PageDown:
                    nextIndex += _visibleRows;
                    break;
                case Keys.Home:
                    nextIndex = 0;
                    break;
                case Keys.End:
                    nextIndex = _items.Length - 1;
                    break;
                case Keys.Enter:
                    if (_selectedIndex >= 0)
                    {
                        ItemCommitted?.Invoke(_selectedIndex);
                    }

                    eventArgs.Handled = true;
                    return;
                case Keys.Escape:
                    CancelRequested?.Invoke();
                    eventArgs.Handled = true;
                    return;
                default:
                    base.OnKeyDown(eventArgs);
                    return;
            }

            _selectedIndex = Math.Clamp(nextIndex, 0, _items.Length - 1);
            EnsureSelectedItemVisible();
            Invalidate();
            eventArgs.Handled = true;
            eventArgs.SuppressKeyPress = true;
            base.OnKeyDown(eventArgs);
        }

        private bool HasScrollBar => _items.Length > _visibleRows;

        private int MaximumFirstVisibleIndex => Math.Max(0, _items.Length - _visibleRows);

        private int ItemIndexAt(Point location)
        {
            var contentWidth = HasScrollBar ? Width - ScrollBarWidth : Width;
            if (location.X < 0 || location.X >= contentWidth || location.Y < 0 || location.Y >= Height)
            {
                return -1;
            }

            var itemIndex = _firstVisibleIndex + location.Y / ItemHeight;
            return itemIndex < _items.Length ? itemIndex : -1;
        }

        private void SetHoveredIndex(int itemIndex)
        {
            if (_hoveredIndex == itemIndex)
            {
                return;
            }

            _hoveredIndex = itemIndex;
            Invalidate();
        }

        private void EnsureSelectedItemVisible()
        {
            if (_selectedIndex < 0)
            {
                return;
            }

            if (_selectedIndex < _firstVisibleIndex)
            {
                _firstVisibleIndex = _selectedIndex;
            }
            else if (_selectedIndex >= _firstVisibleIndex + _visibleRows)
            {
                _firstVisibleIndex = _selectedIndex - _visibleRows + 1;
            }

            _firstVisibleIndex = Math.Clamp(_firstVisibleIndex, 0, MaximumFirstVisibleIndex);
        }

        private void SetFirstVisibleIndex(int index)
        {
            var nextIndex = Math.Clamp(index, 0, MaximumFirstVisibleIndex);
            if (_firstVisibleIndex == nextIndex)
            {
                return;
            }

            _firstVisibleIndex = nextIndex;
            Invalidate();
        }

        private void DrawScrollBar(Graphics graphics)
        {
            var track = new Rectangle(Width - ScrollBarWidth, 0, ScrollBarWidth, Height);
            using var trackBrush = new SolidBrush(PulseReaderTheme.DisabledSurface);
            graphics.FillRectangle(trackBrush, track);
            using var separator = new Pen(PulseReaderTheme.Border);
            graphics.DrawLine(separator, track.Left, track.Top, track.Left, track.Bottom);

            var thumb = ThumbBounds();
            using var thumbBrush = new SolidBrush(
                _draggingThumb ? PulseReaderTheme.Accent : PulseReaderTheme.Border);
            graphics.FillRectangle(thumbBrush, thumb);
        }

        private Rectangle ThumbBounds()
        {
            var thumbHeight = Math.Max(24, Height * _visibleRows / _items.Length);
            var travel = Math.Max(0, Height - thumbHeight);
            var thumbTop = MaximumFirstVisibleIndex == 0
                ? 0
                : (int)Math.Round(travel * (_firstVisibleIndex / (double)MaximumFirstVisibleIndex));
            return new Rectangle(
                Width - ScrollBarWidth + 4,
                thumbTop + 2,
                ScrollBarWidth - 8,
                Math.Max(8, thumbHeight - 4));
        }

        private int FirstVisibleIndexFromThumb(int thumbTop)
        {
            var thumb = ThumbBounds();
            var travel = Math.Max(1, Height - (thumb.Height + 4));
            var fraction = Math.Clamp(thumbTop / (double)travel, 0d, 1d);
            return (int)Math.Round(MaximumFirstVisibleIndex * fraction);
        }
    }

    private sealed class ThemedDropDownRenderer : ToolStripRenderer
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs eventArgs)
        {
            using var background = new SolidBrush(PulseReaderTheme.Surface);
            eventArgs.Graphics.FillRectangle(background, eventArgs.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs eventArgs)
        {
            using var border = new Pen(PulseReaderTheme.Border);
            eventArgs.Graphics.DrawRectangle(
                border,
                0,
                0,
                eventArgs.ToolStrip.Width - 1,
                eventArgs.ToolStrip.Height - 1);
        }
    }
}
