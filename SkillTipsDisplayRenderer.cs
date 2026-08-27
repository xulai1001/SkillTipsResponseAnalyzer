using System.Drawing;
using System.Collections.Immutable;
using System.Text;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.Text;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TAttribute = Terminal.Gui.Drawing.Attribute;
using TColor = Terminal.Gui.Drawing.Color;
using UmamusumeResponseAnalyzer.TerminalGui;
using static SkillTipsResponseAnalyzer.i18n.ParseSkillTipsResponse;

namespace SkillTipsResponseAnalyzer;

internal sealed record SkillTipsDisplaySnapshot(
    int Speed,
    int Stamina,
    int Power,
    int Guts,
    int Wisdom,
    int TotalSkillPoints,
    int UsedSkillPoints,
    int RemainingSkillPoints,
    ImmutableArray<SkillTipsDisplaySkill> Skills,
    int LearnedSkillScore,
    int RecommendedSkillScore,
    int StatusScore,
    int PredictedTotalScore,
    string CurrentRank,
    string NextRank,
    int ScoreToNextRank,
    double RankProgress,
    ImmutableArray<string> KnownIssues,
    string? AverageCostEffectiveness,
    string? MarginalCostEffectiveness,
    ImmutableArray<SkillTipsCostEffectiveness> ExpectedCostEffectiveness,
    ImmutableArray<string> Warnings);

internal sealed record SkillTipsDisplaySkill(string Name, int SkillPoints, int Score);

internal sealed record SkillTipsCostEffectiveness(int SkillPoints, string Value);

internal static class SkillTipsDisplayRenderer
{
    const int TextViewEndOfLineCellWidth = 1;
    const int ScrollBarCellWidth = 1;
    const int FrameBorderCellWidth = 2;

    public static WorkspaceContent Render(SkillTipsDisplaySnapshot display)
        => new(() => new SkillTipsDashboardView(display));

    static string[] WrapLines(IEnumerable<string> lines, int width)
        => [.. lines.SelectMany(line => line.Length == 0
            ? [string.Empty]
            : TextFormatter.WordWrapText(line, Math.Max(1, width)))];

    sealed class SkillTipsDashboardView : View
    {
        readonly FrameView skills;
        readonly SkillTipsTableView table;
        readonly SkillTipsDetailsView details;
        readonly int requiredSkillsWidth;
        Size lastFrameSize;
        bool? lastWide;

        public SkillTipsDashboardView(SkillTipsDisplaySnapshot display)
        {
            Id = "skill-tips-root";
            Width = Dim.Fill();
            Height = Dim.Fill();
            CanFocus = true;
            TabStop = TabBehavior.NoStop;
            ViewportSettings = ViewportSettingsFlags.None;
            SetScheme(SkillTipsPalette.BaseScheme);

            (table, requiredSkillsWidth) = CreateSkillTable(display.Skills);
            skills = new FrameView
            {
                Id = "skill-tips-skills",
                Title = I18N_RecommendedSkills,
                BorderStyle = LineStyle.Single,
                CanFocus = true,
                TabStop = TabBehavior.NoStop
            };
            skills.SetScheme(SkillTipsPalette.BaseScheme);
            skills.Add(table);

            details = new(display)
            {
                Id = "skill-tips-details"
            };
            table.NextFocus = details.CostEffectiveness;
            table.PreviousFocus = details.CostEffectiveness;
            details.CostEffectiveness.NextFocus = table.Body;
            details.CostEffectiveness.PreviousFocus = table.Body;
            table.Body.HasFocusChanged += (_, _) =>
            {
                if (table.Body.HasFocus)
                    RevealVertically(skills.Frame.Y);
            };
            details.CostEffectiveness.HasFocusChanged += (_, _) =>
            {
                if (details.CostEffectiveness.HasFocus)
                    RevealVertically(details.Frame.Y + details.CostEffectiveness.Frame.Y);
            };
            Add(skills, details);
        }

        protected override void OnSubViewLayout(LayoutEventArgs args)
        {
            var frameWidth = Math.Max(0, Frame.Width);
            var frameHeight = Math.Max(0, Frame.Height);
            var gap = frameWidth == 0 ? 0 : 1;
            var wideDetailsWidth = Math.Max(1, frameWidth - requiredSkillsWidth - gap);
            var wide =
                frameWidth >= requiredSkillsWidth + gap + details.PreferredWidth
                && frameHeight >= details.GetMinimumHeight(wideDetailsWidth);
            var revealFocusedRegion = lastFrameSize != Frame.Size || lastWide != wide;
            var viewportSettings = wide
                ? ViewportSettingsFlags.None
                : ViewportSettingsFlags.HasScrollBars;
            if (ViewportSettings != viewportSettings)
                ViewportSettings = viewportSettings;
            int contentWidth;
            int contentHeight;
            if (wide)
            {
                contentWidth = frameWidth;
                contentHeight = frameHeight;
                skills.Frame = new(0, 0, requiredSkillsWidth, contentHeight);
                details.Frame = new(
                    requiredSkillsWidth + gap,
                    0,
                    wideDetailsWidth,
                    contentHeight);
            }
            else
            {
                var viewportWidthWithVerticalScrollBar = Math.Max(
                    1,
                    frameWidth - ScrollBarCellWidth);
                contentWidth = Math.Max(
                    viewportWidthWithVerticalScrollBar,
                    Math.Max(
                        I18N_RecommendedSkills.GetColumns() + 4,
                        details.MinimumAccessibleWidth));
                var horizontalScrollBarHeight =
                    contentWidth > viewportWidthWithVerticalScrollBar
                        ? ScrollBarCellWidth
                        : 0;
                var visibleHeight = Math.Max(1, frameHeight - horizontalScrollBarHeight);
                var skillsHeight = Math.Max(8, visibleHeight / 2);
                var detailsHeight = Math.Max(
                    details.GetMinimumHeight(contentWidth),
                    visibleHeight - skillsHeight - gap);
                contentHeight = skillsHeight + gap + detailsHeight;
                skills.Frame = new(0, 0, contentWidth, skillsHeight);
                details.Frame = new(0, skillsHeight + gap, contentWidth, detailsHeight);
            }

            var contentSize = new Size(contentWidth, contentHeight);
            if (GetContentSize() != contentSize)
                SetContentSize(contentSize);

            var maxX = Math.Max(0, contentWidth - Viewport.Width);
            var maxY = Math.Max(0, contentHeight - Viewport.Height);
            var viewportX = Math.Min(Viewport.X, maxX);
            var viewportY = Math.Min(Viewport.Y, maxY);
            if (viewportX != Viewport.X || viewportY != Viewport.Y)
                Viewport = new(viewportX, viewportY, Viewport.Width, Viewport.Height);

            base.OnSubViewLayout(args);
            if (revealFocusedRegion)
            {
                if (table.Body.HasFocus)
                    RevealVertically(skills.Frame.Y);
                else if (details.CostEffectiveness.HasFocus)
                    RevealVertically(details.Frame.Y + details.CostEffectiveness.Frame.Y);
            }
            lastFrameSize = Frame.Size;
            lastWide = wide;
        }

        void RevealVertically(int y)
        {
            var max = Math.Max(
                0,
                VerticalScrollBar.ScrollableContentSize
                    - VerticalScrollBar.VisibleContentSize);
            VerticalScrollBar.Value = Math.Clamp(y, 0, max);
        }

        static (SkillTipsTableView Table, int RequiredFrameWidth) CreateSkillTable(
            IReadOnlyList<SkillTipsDisplaySkill> rows)
        {
            var columnWidths = new[]
            {
                Math.Max(
                    I18N_Columns_SkillName.GetColumns(),
                    rows.Select(x => x.Name.GetColumns()).DefaultIfEmpty().Max()),
                Math.Max(
                    I18N_Columns_RequireSP.GetColumns(),
                    rows.Select(x => x.SkillPoints.ToString().GetColumns()).DefaultIfEmpty().Max()),
                Math.Max(
                    I18N_Columns_Grade.GetColumns(),
                    rows.Select(x => x.Score.ToString().GetColumns()).DefaultIfEmpty().Max())
            };
            var table = new SkillTipsTableView(rows, columnWidths)
            {
                Id = "skill-tips-table",
                Width = Dim.Fill(),
                Height = Dim.Fill()
            };
            var tableContentWidth = columnWidths.Sum() + columnWidths.Length + 1;
            var requiredFrameWidth = tableContentWidth
                + TextViewEndOfLineCellWidth
                + ScrollBarCellWidth
                + FrameBorderCellWidth;
            return (table, requiredFrameWidth);
        }
    }

#pragma warning disable CS0618 // TextView is the Host-provided native cell-aware scrolling surface.
    sealed class SkillTipsTableView : View
    {
        readonly TextView header;
        readonly int contentWidth;

        public SkillTipsTableView(
            IReadOnlyList<SkillTipsDisplaySkill> rows,
            IReadOnlyList<int> columnWidths)
        {
            CanFocus = true;
            TabStop = TabBehavior.NoStop;
            SetScheme(SkillTipsPalette.BaseScheme);
            contentWidth = columnWidths.Sum() + columnWidths.Count + 1;

            header = new()
            {
                Id = "skill-tips-table-header",
                ReadOnly = true,
                WordWrap = false,
                CanFocus = false,
                TabStop = TabBehavior.NoStop,
                Text = string.Join(
                    Environment.NewLine,
                    FormatRow(
                        I18N_Columns_SkillName,
                        I18N_Columns_RequireSP,
                        I18N_Columns_Grade,
                        columnWidths),
                    $"├{new string('─', columnWidths[0])}" +
                    $"┼{new string('─', columnWidths[1])}" +
                    $"┼{new string('─', columnWidths[2])}┤")
            };
            header.SetScheme(SkillTipsPalette.BaseScheme);
            Body = new(
                [.. rows.Select(row => FormatRow(
                    row.Name,
                    row.SkillPoints.ToString(),
                    row.Score.ToString(),
                    columnWidths))],
                header);
            Add(header, Body);
        }

        public SkillTipsTableBody Body { get; }
        public View? NextFocus
        {
            get => Body.NextFocus;
            set => Body.NextFocus = value;
        }

        public View? PreviousFocus
        {
            get => Body.PreviousFocus;
            set => Body.PreviousFocus = value;
        }

        protected override void OnSubViewLayout(LayoutEventArgs args)
        {
            var width = Math.Max(1, Viewport.Width);
            var height = Math.Max(0, Viewport.Height);
            var headerHeight = Math.Min(2, height);
            header.Frame = new(
                0,
                0,
                Math.Max(0, width - ScrollBarCellWidth),
                headerHeight);
            header.SetContentSize(new(
                contentWidth + TextViewEndOfLineCellWidth,
                2));
            Body.Frame = new(0, headerHeight, width, Math.Max(0, height - headerHeight));
            Body.SyncHeader();
            base.OnSubViewLayout(args);
        }

        static string FormatRow(
            string name,
            string skillPoints,
            string score,
            IReadOnlyList<int> widths)
            => $"│{PadEnd(name, widths[0])}" +
                $"│{PadStart(skillPoints, widths[1])}" +
                $"│{PadStart(score, widths[2])}│";

        static string PadEnd(string value, int width)
            => value + new string(' ', Math.Max(0, width - value.GetColumns()));

        static string PadStart(string value, int width)
            => new string(' ', Math.Max(0, width - value.GetColumns())) + value;
    }

    sealed class SkillTipsTableBody : TextView
    {
        readonly TextView header;

        public SkillTipsTableBody(string[] rows, TextView header)
        {
            this.header = header;
            Id = "skill-tips-table";
            ReadOnly = true;
            WordWrap = false;
            CanFocus = true;
            TabStop = TabBehavior.TabStop;
            ViewportSettings = ViewportSettingsFlags.HasScrollBars;
            Text = string.Join(Environment.NewLine, rows);
            SetScheme(SkillTipsPalette.TableScheme);
        }

        public View? NextFocus { get; set; }
        public View? PreviousFocus { get; set; }

        protected override bool OnMouseEvent(Mouse mouse)
        {
            if (mouse.Flags.HasFlag(MouseFlags.WheeledRight))
                ScrollHorizontal(4);
            else if (mouse.Flags.HasFlag(MouseFlags.WheeledLeft))
                ScrollHorizontal(-4);
            else if (mouse.Flags.HasFlag(MouseFlags.WheeledDown))
                VerticalScrollBar.Value += VerticalScrollBar.Increment;
            else if (mouse.Flags.HasFlag(MouseFlags.WheeledUp))
                VerticalScrollBar.Value -= VerticalScrollBar.Increment;
            else
                return base.OnMouseEvent(mouse);

            SetFocus();
            return true;
        }

        public void SyncHeader()
        {
            if (header is null)
                return;

            var maxX = Math.Max(
                0,
                header.GetContentSize().Width - header.Viewport.Width);
            var x = Math.Min(Viewport.X, maxX);
            if (header.Viewport.X != x)
                header.Viewport = new(x, 0, header.Viewport.Width, header.Viewport.Height);
        }

        protected override void OnViewportChanged(DrawEventArgs args)
        {
            base.OnViewportChanged(args);
            SyncHeader();
        }

        protected override bool OnKeyDown(Key key)
        {
            if (key == Key.Tab && NextFocus?.SetFocus() == true)
                return true;
            if (key == Key.Tab.WithShift && PreviousFocus?.SetFocus() == true)
                return true;
            if (key == Key.CursorLeft.WithShift)
                return ScrollHorizontal(-4) is not false;
            if (key == Key.CursorRight.WithShift)
                return ScrollHorizontal(4) is not false;
            return base.OnKeyDown(key);
        }

    }
#pragma warning restore CS0618

    sealed class SkillTipsDetailsView : View
    {
        const int Gap = 1;
        const int MinimumCostHeight = 3;
        readonly SkillTipsSummaryView summary;
        readonly FrameView knownIssues;
        readonly Label knownIssuesText;
        readonly string[] knownIssueLines;

        public SkillTipsDetailsView(SkillTipsDisplaySnapshot display)
        {
            CanFocus = true;
            TabStop = TabBehavior.NoStop;
            SetScheme(SkillTipsPalette.BaseScheme);

            summary = new(display)
            {
                Id = "skill-tips-summary"
            };
            knownIssueLines = [.. display.KnownIssues.Skip(1)];
            knownIssues = new()
            {
                Id = "skill-tips-known-issues",
                Title = display.KnownIssues[0],
                BorderStyle = LineStyle.Single,
                CanFocus = false,
                TabStop = TabBehavior.NoStop
            };
            knownIssues.SetScheme(SkillTipsPalette.BaseScheme);
            knownIssues.Border.GetOrCreateView().SetScheme(SkillTipsPalette.WarningScheme);
            knownIssuesText = new()
            {
                X = 1,
                Y = 0,
                CanFocus = false,
                HotKeySpecifier = new Rune(0)
            };
            knownIssuesText.SetScheme(SkillTipsPalette.BaseScheme);
            knownIssues.Add(knownIssuesText);

            CostEffectiveness = new(
            [
                .. display.AverageCostEffectiveness is null
                    ? []
                    : new[] { string.Format(I18N_AverageCostEffectiveness, display.AverageCostEffectiveness) },
                .. display.MarginalCostEffectiveness is null
                    ? []
                    : new[] { string.Format(I18N_MarginalCostEffectiveness, display.MarginalCostEffectiveness) },
                I18N_ExpectedCostEffectiveness,
                .. display.ExpectedCostEffectiveness.Select(x =>
                    string.Format(I18N_ExpectedCostEffectivenessByPrice, x.SkillPoints, x.Value))
            ]);
            Add(summary, knownIssues, CostEffectiveness);

            PreferredWidth = Math.Max(
                summary.PreferredWidth,
                Math.Max(
                    display.KnownIssues[0].GetColumns() + 4,
                    I18N_CostEffectiveness.GetColumns() + 4));
            MinimumAccessibleWidth = new[]
            {
                I18N_ScoreSummary,
                I18N_PredictedRank,
                display.KnownIssues[0],
                I18N_CostEffectiveness
            }.Max(x => x.GetColumns()) + 4;
        }

        public SkillTipsCostEffectivenessView CostEffectiveness { get; }
        public int PreferredWidth { get; }
        public int MinimumAccessibleWidth { get; }

        public int GetMinimumHeight(int width)
            => summary.GetRequiredHeight(width)
                + Gap
                + GetKnownIssuesHeight(width)
                + Gap
                + MinimumCostHeight;

        protected override void OnSubViewLayout(LayoutEventArgs args)
        {
            var width = Math.Max(1, Viewport.Width);
            var y = 0;
            var summaryHeight = summary.GetRequiredHeight(width);
            summary.Frame = new(0, y, width, summaryHeight);
            y += summaryHeight + Gap;

            var knownIssuesHeight = GetKnownIssuesHeight(width);
            knownIssues.Frame = new(0, y, width, knownIssuesHeight);
            LayoutKnownIssues(width, knownIssuesHeight);
            y += knownIssuesHeight + Gap;

            CostEffectiveness.Frame = new(
                0,
                y,
                width,
                Math.Max(MinimumCostHeight, Viewport.Height - y));
            base.OnSubViewLayout(args);
        }

        int GetKnownIssuesHeight(int outerWidth)
            => WrapLines(knownIssueLines, Math.Max(1, outerWidth - 4)).Length + 2;

        void LayoutKnownIssues(int outerWidth, int outerHeight)
        {
            var wrapWidth = Math.Max(1, outerWidth - 4);
            var wrapped = WrapLines(knownIssueLines, wrapWidth);
            knownIssuesText.Frame = new(1, 0, wrapWidth, Math.Max(0, outerHeight - 2));
            knownIssuesText.Text = string.Join(Environment.NewLine, wrapped);
        }
    }

    sealed class SkillTipsSummaryView : FrameView
    {
        const int MinimumRankImageRows = 7;
        const int FrameBorderRows = 2;
        const int ProgressBarRows = 1;
        readonly Label breakdown;
        readonly FrameView highlight;
        readonly RankImageView predictedRank;
        readonly Label predictedTotal;
        readonly Label nextRankDistance;
        readonly ProgressBar rankProgress;
        readonly string[] breakdownLines;
        readonly string rankText;
        readonly string totalText;
        readonly string distanceText;
        readonly int breakdownPreferredWidth;
        readonly int predictionMinimumWidth;

        public SkillTipsSummaryView(SkillTipsDisplaySnapshot display)
        {
            Title = I18N_ScoreSummary;
            BorderStyle = LineStyle.Single;
            CanFocus = false;
            TabStop = TabBehavior.NoStop;
            SetScheme(SkillTipsPalette.BaseScheme);

            breakdownLines =
            [
                $"{I18N_Speed}: {display.Speed}",
                $"{I18N_Stamina}: {display.Stamina}",
                $"{I18N_Power}: {display.Power}",
                $"{I18N_Guts}: {display.Guts}",
                $"{I18N_Wisdom}: {display.Wisdom}",
                string.Empty,
                $"{I18N_TotalSkillPoints}: {display.TotalSkillPoints}",
                $"{I18N_UsedSkillPoints}: {display.UsedSkillPoints}",
                $"{I18N_RemainingSkillPoints}: {display.RemainingSkillPoints}",
                string.Empty,
                $"{I18N_LearnedSkillScore}: {display.LearnedSkillScore}",
                $"{I18N_RecommendedSkillScore}: {display.RecommendedSkillScore}",
                $"{I18N_StatusScore}: {display.StatusScore}"
            ];
            rankText = display.CurrentRank;
            totalText = $"{I18N_PredictedTotalScore}: {display.PredictedTotalScore}";
            distanceText = string.Format(
                I18N_ScoreToNextGrade,
                display.NextRank,
                display.ScoreToNextRank);
            breakdownPreferredWidth = breakdownLines
                .Where(x => x.Length != 0)
                .Select(x => x.GetColumns())
                .DefaultIfEmpty(1)
                .Max();
            predictionMinimumWidth = Math.Max(
                I18N_PredictedRank.GetColumns() + 2,
                new[] { rankText, totalText, distanceText }
                    .Max(x => x.GetColumns()) + 2);
            PreferredWidth = breakdownPreferredWidth + 1 + predictionMinimumWidth + 2;

            breakdown = new()
            {
                Id = "skill-tips-summary-breakdown",
                CanFocus = false,
                HotKeySpecifier = new Rune(0)
            };
            breakdown.SetScheme(SkillTipsPalette.BaseScheme);
            highlight = new()
            {
                Id = "skill-tips-summary-highlight",
                Title = I18N_PredictedRank,
                BorderStyle = LineStyle.Single,
                CanFocus = false,
                TabStop = TabBehavior.NoStop
            };
            highlight.SetScheme(SkillTipsPalette.BaseScheme);
            highlight.Border.GetOrCreateView().SetScheme(SkillTipsPalette.FocusBorderScheme);
            predictedRank = new(rankText)
            {
                Id = "skill-tips-predicted-rank"
            };
            predictedTotal = CreateHighlightLabel(
                "skill-tips-predicted-total",
                SkillTipsPalette.BaseScheme,
                Alignment.Center);
            nextRankDistance = CreateHighlightLabel(
                "skill-tips-next-rank-distance",
                SkillTipsPalette.MutedScheme,
                Alignment.Center);
            rankProgress = new()
            {
                Id = "skill-tips-rank-progress",
                Fraction = (float)display.RankProgress,
                ProgressBarFormat = ProgressBarFormat.Simple,
                ProgressBarStyle = ProgressBarStyle.Continuous,
                CanFocus = false,
                TabStop = TabBehavior.NoStop
            };
            rankProgress.SetScheme(SkillTipsPalette.RankProgressScheme);
            highlight.Add(predictedRank, predictedTotal, nextRankDistance, rankProgress);
            Add(breakdown, highlight);
        }

        public int PreferredWidth { get; }

        public int GetRequiredHeight(int outerWidth)
            => BuildLayout(Math.Max(1, outerWidth - 2)).Height + 2;

        protected override void OnSubViewLayout(LayoutEventArgs args)
        {
            var layout = BuildLayout(Math.Max(1, Viewport.Width));
            breakdown.Frame = new(0, 0, layout.BreakdownWidth, layout.BreakdownLines.Length);
            breakdown.Text = string.Join(Environment.NewLine, layout.BreakdownLines);
            highlight.Frame = new(
                layout.HighlightX,
                layout.HighlightY,
                layout.HighlightWidth,
                layout.HighlightHeight);
            LayoutHighlight(layout.HighlightWidth, layout.HighlightHeight);
            base.OnSubViewLayout(args);
        }

        (
            string[] BreakdownLines,
            int BreakdownWidth,
            int HighlightX,
            int HighlightY,
            int HighlightWidth,
            int HighlightHeight,
            int Height)
            BuildLayout(int contentWidth)
        {
            var sideBySide =
                contentWidth >= breakdownPreferredWidth + 1 + predictionMinimumWidth;
            var breakdownWidth = sideBySide
                ? breakdownPreferredWidth
                : contentWidth;
            var highlightX = sideBySide ? breakdownWidth + 1 : 0;
            var highlightWidth = sideBySide
                ? contentWidth - highlightX
                : contentWidth;
            var breakdownWrapped = WrapLines(breakdownLines, breakdownWidth);
            var predictionMinimumHeight = GetPredictionMinimumHeight(highlightWidth);
            var highlightHeight = sideBySide
                ? Math.Max(breakdownWrapped.Length, predictionMinimumHeight)
                : predictionMinimumHeight;
            var highlightY = sideBySide ? 0 : breakdownWrapped.Length + 1;
            var height = sideBySide
                ? highlightHeight
                : highlightY + highlightHeight;
            return (
                breakdownWrapped,
                breakdownWidth,
                highlightX,
                highlightY,
                highlightWidth,
                highlightHeight,
                height);
        }

        int GetPredictionMinimumHeight(int outerWidth)
        {
            var innerWidth = Math.Max(1, outerWidth - 2);
            return MinimumRankImageRows
                + WrapLines([totalText], innerWidth).Length
                + WrapLines([distanceText], innerWidth).Length
                + ProgressBarRows
                + FrameBorderRows;
        }

        void LayoutHighlight(int outerWidth, int outerHeight)
        {
            var innerWidth = Math.Max(1, outerWidth - FrameBorderRows);
            var totalLines = WrapLines([totalText], innerWidth);
            var distanceLines = WrapLines([distanceText], innerWidth);
            var rankImageRows = Math.Max(
                MinimumRankImageRows,
                outerHeight
                    - FrameBorderRows
                    - totalLines.Length
                    - distanceLines.Length
                    - ProgressBarRows);
            var y = 0;
            predictedRank.Frame = new(0, y, innerWidth, rankImageRows);
            y += rankImageRows;
            predictedTotal.Frame = new(0, y, innerWidth, totalLines.Length);
            predictedTotal.Text = string.Join(Environment.NewLine, totalLines);
            y += totalLines.Length;
            nextRankDistance.Frame = new(0, y, innerWidth, distanceLines.Length);
            nextRankDistance.Text = string.Join(Environment.NewLine, distanceLines);
            y += distanceLines.Length;
            rankProgress.Frame = new(0, y, innerWidth, ProgressBarRows);
        }

        static Label CreateHighlightLabel(string id, Scheme scheme, Alignment alignment)
        {
            var label = new Label
            {
                Id = id,
                CanFocus = false,
                HotKeySpecifier = new Rune(0),
                TextAlignment = alignment
            };
            label.SetScheme(scheme);
            return label;
        }
    }

    sealed class RankImageView : ImageView
    {
        readonly string rank;
        (string Rank, int PixelWidth, int PixelHeight)? renderedImageKey;

        public RankImageView(string rank)
        {
            this.rank = rank;
            UseRasterGraphics = true;
            UseBackgroundRendering = false;
            CanFocus = false;
            TabStop = TabBehavior.NoStop;
        }

        protected override bool OnDrawingContent(DrawContext? context)
        {
            EnsureRankImage();
            return base.OnDrawingContent(context);
        }

        void EnsureRankImage()
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("预测等级字体栅格化仅支持 Windows。");

            Size pixelSize;
            try
            {
                pixelSize = ViewportToScreenInPixels().Size;
            }
            catch (InvalidOperationException exception)
            {
                throw new NotSupportedException(
                    "预测等级图像需要 Kitty 或 Sixel raster graphics 支持。",
                    exception);
            }

            if (pixelSize.Width <= 0 || pixelSize.Height <= 0)
                return;

            var key = (rank, pixelSize.Width, pixelSize.Height);
            if (renderedImageKey == key)
                return;

            Image = RankImageRasterizer.Rasterize(
                rank,
                pixelSize.Width,
                pixelSize.Height,
                new TColor(StandardColor.BrightCyan),
                TColor.Black);
            renderedImageKey = key;
        }
    }

    sealed class SkillTipsCostEffectivenessView : FrameView
    {
        readonly string[] lines;
        readonly Label text;

        public SkillTipsCostEffectivenessView(string[] lines)
        {
            this.lines = lines;
            Id = "skill-tips-cost-effectiveness";
            Title = I18N_CostEffectiveness;
            BorderStyle = LineStyle.Single;
            CanFocus = true;
            TabStop = TabBehavior.TabStop;
            ViewportSettings = ViewportSettingsFlags.HasVerticalScrollBar;
            SetScheme(SkillTipsPalette.BaseScheme);
            text = new()
            {
                Id = "skill-tips-cost-effectiveness-text",
                X = 1,
                Y = 0,
                CanFocus = false,
                HotKeySpecifier = new Rune(0)
            };
            text.SetScheme(SkillTipsPalette.BaseScheme);
            Add(text);
            HasFocusChanged += (_, _) => Border.GetOrCreateView().SetScheme(
                HasFocus ? SkillTipsPalette.FocusBorderScheme : SkillTipsPalette.BaseScheme);
            MouseEvent += (_, mouse) =>
            {
                if (mouse.Flags.HasFlag(MouseFlags.WheeledDown))
                    ScrollVertical(1);
                else if (mouse.Flags.HasFlag(MouseFlags.WheeledUp))
                    ScrollVertical(-1);
                else
                    return;

                SetFocus();
                mouse.Handled = true;
            };
        }

        public View? NextFocus { get; set; }
        public View? PreviousFocus { get; set; }

        protected override void OnSubViewLayout(LayoutEventArgs args)
        {
            var width = Math.Max(1, Viewport.Width);
            var wrapWidth = Math.Max(1, width - 2);
            var wrapped = WrapLines(lines, wrapWidth);
            text.Frame = new(1, 0, wrapWidth, wrapped.Length);
            text.Text = string.Join(Environment.NewLine, wrapped);
            var contentSize = new Size(width, Math.Max(Viewport.Height, wrapped.Length));
            if (GetContentSize() != contentSize)
                SetContentSize(contentSize);

            var maxY = Math.Max(0, contentSize.Height - Viewport.Height);
            if (Viewport.Y > maxY)
                Viewport = new(0, maxY, Viewport.Width, Viewport.Height);
            base.OnSubViewLayout(args);
        }

        protected override bool OnKeyDown(Key key)
        {
            if (key == Key.Tab && NextFocus?.SetFocus() == true)
                return true;
            if (key == Key.Tab.WithShift && PreviousFocus?.SetFocus() == true)
                return true;
            if (key == Key.CursorDown)
                return ScrollVertical(1) is not false;
            if (key == Key.CursorUp)
                return ScrollVertical(-1) is not false;
            if (key == Key.PageDown)
                return ScrollVertical(Math.Max(1, Viewport.Height - 1)) is not false;
            if (key == Key.PageUp)
                return ScrollVertical(-Math.Max(1, Viewport.Height - 1)) is not false;
            return base.OnKeyDown(key);
        }

    }

    static class SkillTipsPalette
    {
        static readonly TAttribute HostNormal = new(
            new TColor(StandardColor.White),
            TColor.Black);
        static readonly TAttribute Normal = new(
            HostNormal.Foreground,
            TColor.Black,
            HostNormal.Style);
        static readonly TAttribute Focus = new(
            TColor.Black,
            new TColor(StandardColor.BrightCyan),
            HostNormal.Style);
        static readonly TAttribute Cyan = new(
            new TColor(StandardColor.BrightCyan),
            TColor.Black,
            HostNormal.Style);
        static readonly TAttribute Amber = new(
            new TColor(StandardColor.BrightYellow),
            TColor.Black,
            HostNormal.Style);
        static readonly TAttribute Muted = new(
            new TColor(StandardColor.Gray),
            TColor.Black,
            HostNormal.Style);

        public static Scheme BaseScheme { get; } = new()
        {
            Normal = Normal,
            ReadOnly = Normal,
            Focus = Cyan
        };

        public static Scheme TableScheme { get; } = new()
        {
            Normal = Normal,
            ReadOnly = Normal,
            Focus = Focus,
            HotNormal = Cyan,
            HotFocus = Focus
        };

        public static Scheme FocusBorderScheme { get; } = new(Cyan);
        public static Scheme RankProgressScheme { get; } = new(Cyan);
        public static Scheme WarningScheme { get; } = new(Amber);
        public static Scheme MutedScheme { get; } = new(Muted);
    }
}
