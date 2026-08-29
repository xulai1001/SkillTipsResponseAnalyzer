using System.Drawing;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Gallop;
using Gallop.Endpoints;
using MessagePack;
using Newtonsoft.Json;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Terminal.Gui.Text;
using Terminal.Gui.Time;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using UmamusumeResponseAnalyzer;
using UmamusumeResponseAnalyzer.Entities;
using UmamusumeResponseAnalyzer.TerminalGui;
using UmamusumeResponseAnalyzer.Plugin;
using static SkillTipsResponseAnalyzer.i18n.ParseSkillTipsResponse;
using SkillTipsPlugin = SkillTipsResponseAnalyzer.SkillTipsResponseAnalyzer;
using UraSkillData = UmamusumeResponseAnalyzer.Entities.SkillData;

if (args.Length > 1)
    throw new ArgumentException("Expected at most one raw SingleModeRamen CheckEvent or Load response path.");

CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("zh-CN");
CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");

const int SkillCount = 40;
const int TotalSkillPoints = 600;
const int RamenWarningCardId = 246_813_579;
const string LongSkillHead = "这是一个用于验证纯文本";
const string LongSkillTail = "LONG_SKILL_NAME_TAIL";
var packetPath = args.SingleOrDefault() is { } path ? Path.GetFullPath(path) : null;
var originalCwd = Directory.GetCurrentDirectory();
var smokeDirectory = Path.Combine(Path.GetTempPath(), "skill-tips-response-analyzer-smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(smokeDirectory);

try
{
    Directory.SetCurrentDirectory(smokeDirectory);
    InitializeHostConfigForSmoke();

    using var ui = new WorkspaceSmokeSession();
    var context = new SmokePluginContext(ui.Application);
    var plugin = new SkillTipsPlugin();

    plugin.Initialize(context);
    AssertTrue(
        ReferenceEquals(Workspace.Current, ui.Bootstrap)
        && !ui.CaptureScreen().Contains("SkillTipsResponseAnalyzer", StringComparison.Ordinal),
        "Initialize must not expose the SkillTipsResponseAnalyzer workspace.");

    var analysisEndpoints = GetSingleModeAnalysisEndpoints();
    var finishEndpoints = GetSingleModeFinishEndpoints();
    AssertAnalyzerContracts(context.AnalyzerRegistry, analysisEndpoints, finishEndpoints);
    await AssertFinishWithoutWorkspaceDoesNotCreate(ui, finishEndpoints);

    AssertEqual("UG", new SkillTipsResponseAnalyzer.GradeRank { Id = 19 }.Rank, "Unexpected base UG rank.");
    AssertEqual("UG9", new SkillTipsResponseAnalyzer.GradeRank { Id = 28 }.Rank, "Unexpected UG level suffix.");
    AssertEqual("LG", new SkillTipsResponseAnalyzer.GradeRank { Id = 99 }.Rank, "Unexpected base LG rank.");
    AssertEqual("LF24", new SkillTipsResponseAnalyzer.GradeRank { Id = 148 }.Rank, "Unexpected LF level suffix.");
    AssertEqual("LE以上", new SkillTipsResponseAnalyzer.GradeRank { Id = 149 }.Rank, "Unexpected top-rank label.");

    var expectedSkills = CreateSkills();
    await InitializeSmokeDatabase(expectedSkills);
    await AssertAnalysisEndpointDispatch(ui, analysisEndpoints, finishEndpoints[0]);

    var checkEventEndpoint = GameEndpointCatalog.ByEndpointType[typeof(GameApi.SingleMode.CheckEvent)];
    await DispatchHostResponse(
        checkEventEndpoint.Path,
        MessagePackSerializer.Serialize(CreateCheckEventResponse()));
    var workspace = RequireSkillTipsWorkspace(ui);
    AssertPanelContract(ui, workspace, "CheckEvent");
    AssertTrue(
        ui.CaptureScreen().Contains(I18N_RecommendedSkills, StringComparison.Ordinal),
        "The real Host framebuffer must show the SkillTips recommendation heading.");

    var ramenLoadEndpoint = GameEndpointCatalog.ByEndpointType[typeof(GameApi.SingleModeRamen.Load)];
    var ramenSentinel = RamenWarningCardId.ToString(CultureInfo.InvariantCulture);
    var ramenLinesBefore = ui.CaptureScreen().ReplaceLineEndings("\n").Split('\n');
    var warningsBeforeRamen = ramenLinesBefore
        .Where(line => line.Contains("WARN ", StringComparison.Ordinal))
        .ToArray();
    AssertTrue(
        ramenLinesBefore.All(line => !line.Contains(ramenSentinel, StringComparison.Ordinal)),
        "The Ramen warning sentinel must be unique before dispatch.");
    AssertTrue(
        workspace.RemovePanel("skill-plan"),
        "The Ramen dispatch fixture must remove the previous skill-plan panel.");
    var ramenResponse = CreateTerminalRamenResponse(RamenWarningCardId);
    ramenResponse.data.single_mode_load_common.chara_info.skill_tips_array =
    [
        .. ramenResponse.data.single_mode_load_common.chara_info.skill_tips_array
            .Where(tip => tip.group_id != 99999)
    ];
    await DispatchHostResponse(
        ramenLoadEndpoint.Path,
        MessagePackSerializer.Serialize(ramenResponse));

    AssertTrue(
        ReferenceEquals(workspace, RequireSkillTipsWorkspace(ui)),
        "SkillTipsResponseAnalyzer must reuse its canonical workspace.");
    AssertPanelContract(ui, workspace, "SingleModeRamen.Load");
    var ramenLinesAfter = ui.CaptureScreen().ReplaceLineEndings("\n").Split('\n');
    var warningsAfterRamen = ramenLinesAfter
        .Where(line => line.Contains("WARN ", StringComparison.Ordinal))
        .ToArray();
    AssertTrue(
        warningsAfterRamen.Length > warningsBeforeRamen.Length,
        "The Ramen dispatch must append a visible Warning notification.");
    var notificationHeaderIndexes = Enumerable.Range(0, ramenLinesAfter.Length - 1)
        .Where(index =>
            ramenLinesAfter[index].Contains("WARN ", StringComparison.Ordinal)
            && ramenLinesAfter[index + 1].Contains(ramenSentinel, StringComparison.Ordinal))
        .ToArray();
    AssertEqual(
        1,
        notificationHeaderIndexes.Length,
        "The newest Ramen Warning card must have one visible header/content pair with its unique sentinel.");
    var notificationHeaderIndex = notificationHeaderIndexes[0];
    var notificationHeader = ramenLinesAfter[notificationHeaderIndex];
    AssertTrue(
        notificationHeader.Contains("WARN ", StringComparison.Ordinal)
        && !notificationHeader.Contains("SkillTipsResponseAnalyzer", StringComparison.Ordinal),
        "The newest visible Warning card must show severity without a hidden plugin source.");
    AssertTrue(
        ramenLinesAfter[notificationHeaderIndex + 1].Contains(ramenSentinel, StringComparison.Ordinal),
        "The newest Ramen Warning card content line must contain its unique numeric sentinel.");
    var notificationContent = ramenLinesAfter[notificationHeaderIndex + 1];
    AssertTrue(
        !notificationContent.Contains("[red]", StringComparison.Ordinal)
        && !notificationContent.Contains("[/]", StringComparison.Ordinal),
        "Plain warning notifications must not interpret or contain rich-text tags.");
    AssertTrue(
        ReferenceEquals(Workspace.Current, workspace),
        "Publishing skill-plan must switch to the SkillTips workspace.");

    await AssertUnknownBoughtSkillFailsFast(checkEventEndpoint);
    var longFrame = ui.CaptureScreen(320, 96);
    await AssertStructuredDashboard(ui, checkEventEndpoint, expectedSkills);
    var shortSkills = CreateSkills(includeLongName: false);
    await InitializeSmokeDatabase(shortSkills);
    AssertTrue(
        workspace.RemovePanel("skill-plan"),
        "The short-layout fixture must remove the previous skill-plan panel.");
    await DispatchHostResponse(
        ramenLoadEndpoint.Path,
        MessagePackSerializer.Serialize(CreateTerminalRamenResponse()));
    var shortFrame = ui.CaptureScreen(320, 96);

    AssertSkillNameRendering(longFrame, expectedSkills, shortFrame, shortSkills);
    await InitializeSmokeDatabase(expectedSkills);
    await AssertCostEffectivenessDisplayConditions(ui, checkEventEndpoint);
    await AssertFinishWorkspaceLifecycle(ui, finishEndpoints);

    if (packetPath is not null)
    {
        await DispatchHostResponse(
            finishEndpoints[0].Path,
            SerializeResponse(finishEndpoints[0]));
        AssertNoSkillTipsWorkspace(ui, "Raw replay must begin without a SkillTips workspace.");

        Directory.SetCurrentDirectory(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UmamusumeResponseAnalyzer"));
        if (await Database.Initialize() != DatabaseAvailability.Ready)
            throw new InvalidOperationException("The installed Host database is unavailable for raw replay.");

        var payload = File.ReadAllBytes(packetPath);
        var endpoint = ResolveRamenPacketEndpoint(payload);
        var response = MessagePackSerializer.Deserialize(endpoint.ResponseType, payload)
            ?? throw new InvalidOperationException(
                $"Raw packet did not deserialize to {endpoint.ResponseType.FullName}.");
        var (charaInfo, uncheckedEvents) = GetRamenAnalysisData(response);
        AssertTrue(
            charaInfo.state is 2 or 3 && uncheckedEvents.Length == 0,
            "Raw Ramen packet must satisfy the unchanged terminal condition.");

        await DispatchHostResponse(endpoint.Path, payload);
        var replayWorkspace = RequireSkillTipsWorkspace(ui);
        AssertSkillPlanPanelContract(ui, replayWorkspace, endpoint.Path);
        Console.WriteLine(
            $"Replayed {Path.GetFileName(packetPath)} as {endpoint.Path}: " +
            $"state={charaInfo.state}, unchecked={uncheckedEvents.Length}, " +
            $"skillTips={charaInfo.skill_tips_array.Length}, skillPoint={charaInfo.skill_point}.");
    }

    var disposedWorkspace = RequireSkillTipsWorkspace(ui);
    plugin.Dispose();
    AssertNoSkillTipsWorkspace(ui, "Dispose must remove the owned SkillTips workspace.");
    AssertThrows<InvalidOperationException>(
        disposedWorkspace.SwitchTo,
        "Dispose must tombstone the owned SkillTips workspace generation.");

    var replacementPlugin = new SkillTipsPlugin();
    context.AnalyzerRegistry.Clear();
    replacementPlugin.Initialize(context);
    await DispatchHostResponse(
        checkEventEndpoint.Path,
        MessagePackSerializer.Serialize(CreateCheckEventResponse()));
    var replacementWorkspace = RequireSkillTipsWorkspace(ui);
    AssertTrue(
        !ReferenceEquals(disposedWorkspace, replacementWorkspace),
        "The next output after Dispose must create a new workspace generation.");
    AssertTrue(
        ReferenceEquals(replacementWorkspace, Workspace.Create("skilltipsresponseanalyzer")),
        "Workspace.Create must be OrdinalIgnoreCase within the active generation.");
    var lateContent = WorkspaceContent.Text("late skill plan");

    await DispatchHostResponse(
        finishEndpoints[0].Path,
        SerializeResponse(finishEndpoints[0]));
    AssertNoSkillTipsWorkspace(ui, "Final finish must tombstone the SkillTips workspace generation.");
    AssertTrue(
        !ui.CaptureScreen().Contains(I18N_RecommendedSkills, StringComparison.Ordinal),
        "Final finish must remove the SkillTips panel.");
    AssertTrue(
        !ReferenceEquals(Workspace.Current, replacementWorkspace)
        && !ui.CaptureScreen().Contains("WARN ", StringComparison.Ordinal),
        "Final finish must clear visible state for the tombstoned generation.");

    AssertThrows<InvalidOperationException>(
        () => replacementWorkspace.SetPanel(
            "skill-plan",
            "技能评分建议",
            lateContent,
            fullBleed: true),
        "A late callback must not publish through a tombstoned workspace generation.");
    var nextGeneration = Workspace.Create("skilltipsresponseanalyzer");
    AssertTrue(
        !ReferenceEquals(replacementWorkspace, nextGeneration),
        "Recreating a removed workspace must produce a new canonical generation.");
    nextGeneration.SwitchTo();
    AssertTrue(
        !ui.CaptureScreen().Contains(I18N_RecommendedSkills, StringComparison.Ordinal),
        "A late callback must not pollute the replacement workspace generation.");
    AssertTrue(
        !ui.CaptureScreen().Contains("WARN ", StringComparison.Ordinal),
        "A late callback must not add telemetry to the replacement workspace generation.");
    replacementPlugin.Dispose();
    AssertTrue(
        ReferenceEquals(nextGeneration, Workspace.Create("SkillTipsResponseAnalyzer"))
        && !ui.CaptureScreen().Contains(I18N_RecommendedSkills, StringComparison.Ordinal),
        "Dispose after finish must not remove or mutate another active generation.");
    nextGeneration.Remove();
    AssertNoSkillTipsWorkspace(ui, "The replacement-generation fixture must clean up its workspace.");

    Console.WriteLine("SkillTipsResponseAnalyzer smoke passed.");
}
finally
{
    Directory.SetCurrentDirectory(originalCwd);
    if (Directory.Exists(smokeDirectory))
        Directory.Delete(smokeDirectory, recursive: true);
}

static List<UraSkillData> CreateSkills(bool includeLongName = true)
    => [..
        Enumerable.Range(0, SkillCount)
            .Select(index =>
            {
                var name = includeLongName && index == SkillCount - 1
                    ? $"[red]这是一个用于验证纯文本与水平滚动的超长技能名称-{new string('长', 36)}-{LongSkillTail}[/]"
                    : $"推荐技能{index:00}";
                return new UraSkillData
                {
                    Id = 900001 + index,
                    GroupId = 90001 + index,
                    Rarity = 1,
                    Rate = 1,
                    Name = name,
                    DisplayName = name,
                    Cost = 10,
                    Grade = 100 + index,
                    DisplayOrder = index,
                    Propers = []
                };
            })];

static SingleModeCheckEventResponse CreateCheckEventResponse(int skillPoints = TotalSkillPoints) => new()
{
    data = new()
    {
        chara_info = CreateCharaInfo(skillPoints),
        unchecked_event_array = []
    }
};

static SingleModeRamenLoadResponse CreateTerminalRamenResponse(int cardId = 0) => new()
{
    data = new()
    {
        single_mode_load_common = new()
        {
            chara_info = CreateCharaInfo(cardId: cardId),
            unchecked_event_array = []
        }
    }
};

static SingleModeChara CreateCharaInfo(int skillPoints = TotalSkillPoints, int state = 2, int cardId = 0) => new()
{
    state = state,
    card_id = cardId,
    speed = 130,
    stamina = 263,
    power = 108,
    guts = 239,
    wiz = 239,
    skill_point = skillPoints,
    skill_array = [],
    skill_tips_array =
    [
        .. Enumerable.Range(0, SkillCount).Select(index => new SkillTips
        {
            group_id = 90001 + index,
            rarity = 1,
            level = 0
        }),
        new()
        {
            group_id = 99999,
            rarity = 1,
            level = 0
        }
    ],
    chara_effect_id_array = []
};

static void AssertPanelContract(
    WorkspaceSmokeSession ui,
    Workspace workspace,
    string endpoint)
{
    AssertSkillPlanPanelContract(ui, workspace, endpoint);
    AssertTrue(
        ui.CaptureScreen().Contains("WARN ", StringComparison.Ordinal),
        $"{endpoint} must publish a visible Warning notification.");
}

static void AssertSkillPlanPanelContract(
    WorkspaceSmokeSession ui,
    Workspace workspace,
    string endpoint)
{
    AssertTrue(
        ReferenceEquals(Workspace.Current, workspace)
        && ui.CaptureScreen().Contains(I18N_RecommendedSkills, StringComparison.Ordinal),
        $"{endpoint} must switch to the SkillTips workspace.");
}

static async ValueTask AssertUnknownBoughtSkillFailsFast(GameEndpointDescriptor checkEventEndpoint)
{
    const int unknownSkillId = 765432;
    var response = CreateCheckEventResponse();
    response.data.chara_info.skill_tips_array = [];
    response.data.chara_info.skill_array = [new() { skill_id = unknownSkillId, level = 1 }];

    try
    {
        await DispatchHostResponse(
            checkEventEndpoint.Path,
            MessagePackSerializer.Serialize(response));
    }
    catch (KeyNotFoundException exception)
    {
        AssertTrue(
            exception.Message.Contains($"skillId={unknownSkillId}", StringComparison.Ordinal),
            "Unknown purchased skill failures must identify the missing skillId.");
        return;
    }
    catch (Exception exception)
    {
        throw new InvalidOperationException(
            $"Unknown purchased skill dispatch must fail with KeyNotFoundException: " +
            $"skillId={unknownSkillId}, Actual={exception.GetType().FullName}",
            exception);
    }

    throw new InvalidOperationException(
        $"Unknown purchased skill dispatch must fail fast with KeyNotFoundException: skillId={unknownSkillId}");
}

static async ValueTask AssertCostEffectivenessDisplayConditions(
    WorkspaceSmokeSession ui,
    GameEndpointDescriptor checkEventEndpoint)
{
    foreach (var (skillPoints, showsAverage, showsMarginal) in new[]
    {
        (0, false, false),
        (1, true, false),
        (50, true, false),
        (51, true, true)
    })
    {
        await DispatchHostResponse(
            checkEventEndpoint.Path,
            MessagePackSerializer.Serialize(CreateCheckEventResponse(skillPoints)));
        RequireSkillTipsWorkspace(ui);
        var text = ui.CaptureScreen(320, 96);
        var normalized = RemoveLineBreaks(text);
        AssertEqual(
            showsAverage,
            normalized.Contains(I18N_AverageCostEffectiveness.Split('{')[0], StringComparison.Ordinal),
            $"Average cost-effectiveness visibility is incorrect at {skillPoints} skill points.");
        AssertEqual(
            showsMarginal,
            normalized.Contains(I18N_MarginalCostEffectiveness.Split('{')[0], StringComparison.Ordinal),
            $"Marginal cost-effectiveness visibility is incorrect at {skillPoints} skill points.");
        AssertTrue(
            normalized.Contains(I18N_ExpectedCostEffectiveness.Split('，')[0], StringComparison.Ordinal),
            $"The expected cost-effectiveness section must be visible at {skillPoints} skill points.");
    }
}

static async ValueTask AssertStructuredDashboard(
    WorkspaceSmokeSession ui,
    GameEndpointDescriptor checkEventEndpoint,
    IReadOnlyList<UraSkillData> expectedSkills)
{
    var roundTrip = new[]
    {
        ui.CaptureScreen(320, 96),
        ui.CaptureScreen(90, 24),
        ui.CaptureScreen(320, 96)
    };
    var wide = roundTrip[0];
    foreach (var token in new[]
    {
        I18N_RecommendedSkills,
        I18N_Columns_SkillName,
        I18N_Columns_RequireSP,
        I18N_Columns_Grade,
        expectedSkills[0].DisplayName,
        LongSkillTail,
        "[red]",
        I18N_ScoreCalculateAttention_1,
        I18N_ExpectedCostEffectiveness,
        "500pt"
    })
    {
        AssertTrue(
            wide.Contains(token, StringComparison.Ordinal),
            $"The wide framebuffer is missing visible content: {token}");
    }
    AssertTrue(!wide.Contains('…'), "The complete wide dashboard must not render an ellipsis.");

    var narrow = roundTrip[1];
    AssertTrue(
        narrow.Contains(I18N_RecommendedSkills, StringComparison.Ordinal)
        && narrow.Contains(I18N_Columns_SkillName, StringComparison.Ordinal),
        "The narrow framebuffer must keep the recommendation table visible.");
    AssertTrue(
        roundTrip[2].Contains(LongSkillTail, StringComparison.Ordinal)
        && !roundTrip[2].Contains('…'),
        "A wide-narrow-wide layout round trip must restore the complete dashboard.");

    var low = ui.CaptureScreen(90, 12);
    AssertTrue(
        low.Contains(I18N_RecommendedSkills, StringComparison.Ordinal),
        "The low-height framebuffer must keep the recommendation region reachable.");

    await DispatchHostResponse(
        checkEventEndpoint.Path,
        MessagePackSerializer.Serialize(CreateCheckEventResponse()));
    ui.Flush();
    var keyboardReference = ui.CaptureScreen(60, 24, restore: false, flushHost: false);
    var keyboardTablePoint = FindScreenPoint(keyboardReference, I18N_RecommendedSkills, 0, 3);
    var averageCostPrefix = I18N_AverageCostEffectiveness.Split('{')[0];
    string[] costLines =
    [
        string.Format(I18N_AverageCostEffectiveness, "00.000"),
        string.Format(I18N_MarginalCostEffectiveness, "00.000"),
        I18N_ExpectedCostEffectiveness,
        .. Enumerable.Range(1, Math.Min(10, TotalSkillPoints / 50))
            .Select(index => string.Format(
                I18N_ExpectedCostEffectivenessByPrice,
                index * 50,
                "00.000"))
    ];
    const int narrowScreenWidth = 60;
    const int visibleBodyCells = narrowScreenWidth - 1 - 2 - 1;
    var tableContentCells = new[]
    {
        Math.Max(I18N_Columns_SkillName.GetColumns(), expectedSkills.Max(skill => skill.DisplayName.GetColumns())),
        Math.Max(I18N_Columns_RequireSP.GetColumns(), expectedSkills.Max(skill => skill.Cost.ToString().GetColumns())),
        Math.Max(I18N_Columns_Grade.GetColumns(), expectedSkills.Max(skill => skill.Grade.ToString().GetColumns()))
    }.Sum() + 4;
    var tableHorizontalScrollSteps =
        (Math.Max(0, tableContentCells - visibleBodyCells) + 3) / 4 + 1;
    var costScrollBound = costLines.Sum(
        line => Math.Max(1, (line.GetColumns() + visibleBodyCells - 1) / visibleBodyCells)) + 1;
    ui.SendMouse(new()
    {
        ScreenPosition = keyboardTablePoint,
        Flags = MouseFlags.WheeledDown
    });
    keyboardTablePoint = FindScreenPoint(
        ui.CaptureScreen(narrowScreenWidth, 24, restore: false, flushHost: false),
        I18N_RecommendedSkills,
        0,
        3);
    ui.SendMouse(new()
    {
        ScreenPosition = keyboardTablePoint,
        Flags = MouseFlags.WheeledUp
    });

    var keyboardFrames = new string[13];
    keyboardFrames[0] = ui.CaptureScreen(narrowScreenWidth, 24, restore: false, flushHost: false);
    ui.SendKey(Key.PageDown);
    keyboardFrames[1] = ui.CaptureScreen(narrowScreenWidth, 24, restore: false, flushHost: false);
    ui.SendKey(Key.CursorRight.WithShift);
    keyboardFrames[2] = ui.CaptureScreen(narrowScreenWidth, 24, restore: false, flushHost: false);
    var keyboardTableBottom = keyboardFrames[2];
    for (var i = 0;
         i < SkillCount && !keyboardTableBottom.Contains(LongSkillHead, StringComparison.Ordinal);
         i++)
    {
        ui.SendKey(Key.PageDown);
        keyboardTableBottom = ui.CaptureScreen(narrowScreenWidth, 24, restore: false, flushHost: false);
    }
    ui.SendKey(Key.End.WithCtrl);
    keyboardFrames[3] = ui.CaptureScreen(narrowScreenWidth, 24, restore: false, flushHost: false);
    ui.SendKey(Key.Tab);
    keyboardFrames[4] = ui.CaptureScreen(narrowScreenWidth, 24, restore: false, flushHost: false);
    ui.SendKey(Key.PageDown);
    keyboardFrames[5] = ui.CaptureScreen(narrowScreenWidth, 24, restore: false, flushHost: false);
    keyboardFrames[6] = keyboardFrames[5];
    for (var i = 1;
         i < costScrollBound && !keyboardFrames[6].Contains("500pt", StringComparison.Ordinal);
         i++)
    {
        ui.SendKey(Key.PageDown);
        keyboardFrames[6] = ui.CaptureScreen(narrowScreenWidth, 24, restore: false, flushHost: false);
    }
    ui.SendKey(Key.Tab.WithShift);
    keyboardFrames[7] = ui.CaptureScreen(narrowScreenWidth, 24, restore: false, flushHost: false);
    ui.SendKey(Key.Home.WithCtrl);
    keyboardFrames[8] = ui.CaptureScreen(narrowScreenWidth, 24, restore: false, flushHost: false);
    ui.SendKey(Key.Tab);
    keyboardFrames[9] = ui.CaptureScreen(narrowScreenWidth, 24, restore: false, flushHost: false);
    ui.SendKey(Key.PageUp);
    keyboardFrames[10] = ui.CaptureScreen(narrowScreenWidth, 24, restore: false, flushHost: false);
    keyboardFrames[11] = keyboardFrames[10];
    for (var i = 1;
         i < costScrollBound && !keyboardFrames[11].Contains(averageCostPrefix, StringComparison.Ordinal);
         i++)
    {
        ui.SendKey(Key.PageUp);
        keyboardFrames[11] = ui.CaptureScreen(narrowScreenWidth, 24, restore: false, flushHost: false);
    }
    ui.SendKey(Key.Tab.WithShift);
    keyboardFrames[12] = ui.CaptureScreen(narrowScreenWidth, 24, restore: false, flushHost: false);
    AssertTrue(
        keyboardFrames[0].Contains(expectedSkills[0].DisplayName, StringComparison.Ordinal),
        "The table keyboard fixture must begin at the first row.");
    AssertTrue(
        !string.Equals(keyboardFrames[0], keyboardFrames[1], StringComparison.Ordinal),
        "One table PageDown must visibly move the framebuffer downward.");
    AssertTrue(
        !string.Equals(keyboardFrames[1], keyboardFrames[2], StringComparison.Ordinal),
        "One table Shift+Right must visibly move the framebuffer horizontally.");
    AssertTrue(
        keyboardFrames[3].Contains(LongSkillTail, StringComparison.Ordinal),
        "Table keyboard scrolling must reach the final long-name tail.");
    AssertTrue(
        keyboardFrames[4].Contains(averageCostPrefix, StringComparison.Ordinal),
        "The cost-effectiveness keyboard fixture must begin at its first line.");
    AssertTrue(
        !string.Equals(keyboardFrames[4], keyboardFrames[5], StringComparison.Ordinal),
        "One cost-effectiveness PageDown must visibly move its framebuffer downward.");
    AssertTrue(
        keyboardFrames[6].Contains("500pt", StringComparison.Ordinal),
        "Cost-effectiveness PageDown must reach 500pt.");
    AssertTrue(
        keyboardFrames[7].Contains(LongSkillTail, StringComparison.Ordinal),
        "Cost-effectiveness PageDown must not change the table viewport.");
    AssertTrue(
        keyboardFrames[8].Contains(expectedSkills[0].DisplayName, StringComparison.Ordinal),
        "Table keyboard scrolling must return to the first row.");
    AssertTrue(
        keyboardFrames[9].Contains("500pt", StringComparison.Ordinal),
        "Table keyboard scrolling must not change the cost-effectiveness viewport.");
    AssertTrue(
        !string.Equals(keyboardFrames[9], keyboardFrames[10], StringComparison.Ordinal),
        "One cost-effectiveness PageUp must visibly move its framebuffer upward.");
    AssertTrue(
        keyboardFrames[11].Contains(averageCostPrefix, StringComparison.Ordinal),
        "Cost-effectiveness PageUp must return to its first line.");
    AssertTrue(
        keyboardFrames[12].Contains(expectedSkills[0].DisplayName, StringComparison.Ordinal),
        "Cost-effectiveness PageUp must not change the table viewport.");

    await DispatchHostResponse(
        checkEventEndpoint.Path,
        MessagePackSerializer.Serialize(CreateCheckEventResponse()));
    ui.Flush();
    var mouseReference = ui.CaptureScreen(60, 24, restore: false, flushHost: false);
    var tablePoint = FindScreenPoint(mouseReference, I18N_RecommendedSkills, 0, 3);
    ui.SendMouse(new()
    {
        ScreenPosition = tablePoint,
        Flags = MouseFlags.WheeledDown
    });
    var mouseTableFocus = ui.CaptureScreen(60, 24, restore: false, flushHost: false);
    AssertTrue(
        !string.Equals(mouseReference, mouseTableFocus, StringComparison.Ordinal),
        "One table mouse wheel must visibly move the framebuffer downward.");

    var mouseTableVerticalTail = mouseTableFocus;
    for (var i = 1; i < SkillCount + 8; i++)
    {
        tablePoint = FindScreenPoint(mouseTableVerticalTail, I18N_RecommendedSkills, 0, 3);
        ui.SendMouse(new()
        {
            ScreenPosition = tablePoint,
            Flags = MouseFlags.WheeledDown
        });
        mouseTableVerticalTail = ui.CaptureScreen(60, 24, restore: false, flushHost: false);
    }

    var mouseTableTail = mouseTableVerticalTail;
    for (var i = 0; i < tableHorizontalScrollSteps; i++)
    {
        tablePoint = FindScreenPoint(mouseTableTail, I18N_RecommendedSkills, 0, 3);
        ui.SendMouse(new()
        {
            ScreenPosition = tablePoint,
            Flags = MouseFlags.WheeledRight
        });
        mouseTableTail = ui.CaptureScreen(60, 24, restore: false, flushHost: false);
    }
    AssertTrue(
        mouseTableTail.Contains(LongSkillTail, StringComparison.Ordinal),
        "Table mouse scrolling must reach the final long-name tail.");

    ui.SendKey(Key.Tab);
    var mouseCostReference = ui.CaptureScreen(60, 24, restore: false, flushHost: false);
    var costPoint = FindScreenPoint(mouseCostReference, I18N_CostEffectiveness, 0, 1);
    var mouseCostBottom = mouseCostReference;
    for (var i = 0; i < costScrollBound; i++)
    {
        costPoint = FindScreenPoint(mouseCostBottom, I18N_CostEffectiveness, 0, 1);
        ui.SendMouse(new()
        {
            ScreenPosition = costPoint,
            Flags = MouseFlags.WheeledDown
        });
        mouseCostBottom = ui.CaptureScreen(60, 24, restore: false, flushHost: false);
    }
    AssertTrue(
        mouseCostBottom.Contains("500pt", StringComparison.Ordinal),
        "Cost-effectiveness mouse scrolling must reach 500pt.");

    ui.SendKey(Key.Tab.WithShift);
    var mouseTableAfterCost = ui.CaptureScreen(60, 24, restore: false, flushHost: false);
    AssertTrue(
        mouseTableAfterCost.Contains(LongSkillTail, StringComparison.Ordinal),
        "Cost-effectiveness mouse scrolling must not change the table viewport.");

    ui.SendKey(Key.Tab);
    var mouseCostAfterTable = ui.CaptureScreen(60, 24, restore: false, flushHost: false);
    AssertTrue(
        mouseCostAfterTable.Contains("500pt", StringComparison.Ordinal),
        "Returning to cost-effectiveness must preserve its mouse viewport.");

    var mouseCostTop = mouseCostAfterTable;
    for (var i = 0; i < costScrollBound; i++)
    {
        costPoint = FindScreenPoint(mouseCostTop, I18N_CostEffectiveness, 0, 1);
        ui.SendMouse(new()
        {
            ScreenPosition = costPoint,
            Flags = MouseFlags.WheeledUp
        });
        mouseCostTop = ui.CaptureScreen(60, 24, restore: false, flushHost: false);
    }
    AssertTrue(
        mouseCostTop.Contains(averageCostPrefix, StringComparison.Ordinal),
        "Cost-effectiveness mouse scrolling must return to its first line.");

    ui.SendKey(Key.Tab.WithShift);
    var mouseTableReturnReference = ui.CaptureScreen(60, 24, restore: false, flushHost: false);
    AssertTrue(
        mouseTableReturnReference.Contains(LongSkillTail, StringComparison.Ordinal),
        "Cost-effectiveness mouse return must not change the table viewport.");

    var mouseTableVerticalTop = mouseTableReturnReference;
    for (var i = 0; i < SkillCount + 8; i++)
    {
        tablePoint = FindScreenPoint(mouseTableVerticalTop, I18N_RecommendedSkills, 0, 3);
        ui.SendMouse(new()
        {
            ScreenPosition = tablePoint,
            Flags = MouseFlags.WheeledUp
        });
        mouseTableVerticalTop = ui.CaptureScreen(60, 24, restore: false, flushHost: false);
    }

    var mouseTableTop = mouseTableVerticalTop;
    for (var i = 0; i < tableHorizontalScrollSteps; i++)
    {
        tablePoint = FindScreenPoint(mouseTableTop, I18N_RecommendedSkills, 0, 3);
        ui.SendMouse(new()
        {
            ScreenPosition = tablePoint,
            Flags = MouseFlags.WheeledLeft
        });
        mouseTableTop = ui.CaptureScreen(60, 24, restore: false, flushHost: false);
    }
    AssertTrue(
        mouseTableTop.Contains(expectedSkills[0].DisplayName, StringComparison.Ordinal),
        "Table mouse scrolling must return to the first row.");

    ui.SendKey(Key.Tab);
    var mouseCostAfterTableTop = ui.CaptureScreen(60, 24, restore: false, flushHost: false);
    AssertTrue(
        mouseCostAfterTableTop.Contains(averageCostPrefix, StringComparison.Ordinal),
        "Table mouse return must not change the cost-effectiveness viewport.");
    ui.CaptureScreen();
}

static void AssertSkillNameRendering(
    string longFrame,
    IReadOnlyList<UraSkillData> longSkills,
    string shortFrame,
    IReadOnlyList<UraSkillData> shortSkills)
{
    AssertTrue(
        longFrame.Contains(longSkills[^1].DisplayName, StringComparison.Ordinal),
        "The long-name framebuffer must preserve the complete longest skill name.");
    AssertTrue(
        shortFrame.Contains(shortSkills[^1].DisplayName, StringComparison.Ordinal)
        && !shortFrame.Contains(LongSkillTail, StringComparison.Ordinal),
        "The short-name framebuffer must render the short fixture without the long sentinel.");
}

static Point FindScreenPoint(string screen, string text, int xOffset, int yOffset)
{
    var lines = screen.ReplaceLineEndings("\n").Split('\n');
    for (var y = 0; y < lines.Length; y++)
    {
        var index = lines[y].IndexOf(text, StringComparison.Ordinal);
        if (index >= 0)
            return new(lines[y][..index].GetColumns() + xOffset, y + yOffset);
    }

    throw new InvalidOperationException($"The real Host framebuffer is missing '{text}'.");
}

static string RemoveLineBreaks(string value)
    => value.Replace("\r", string.Empty, StringComparison.Ordinal)
        .Replace("\n", string.Empty, StringComparison.Ordinal);

static GameEndpointDescriptor[] GetSingleModeAnalysisEndpoints()
{
    const string prefix = "/umamusume/single_mode";
    return
    [
        .. GameEndpointCatalog.ByEndpointType.Values
            .Where(endpoint =>
            {
                var suffix = endpoint.Path.EndsWith("/check_event", StringComparison.Ordinal)
                    ? "/check_event"
                    : endpoint.Path.EndsWith("/load", StringComparison.Ordinal)
                        ? "/load"
                        : null;
                if (suffix is null || !endpoint.Path.StartsWith(prefix, StringComparison.Ordinal))
                    return false;

                var scenario = endpoint.Path[prefix.Length..^suffix.Length];
                return scenario.Length == 0
                    || scenario.Length > 1
                        && scenario[0] == '_'
                        && !scenario.AsSpan(1).Contains('/');
            })
            .OrderBy(endpoint => endpoint.Path, StringComparer.Ordinal)
    ];
}

static GameEndpointDescriptor[] GetSingleModeFinishEndpoints()
{
    const string prefix = "/umamusume/single_mode";
    const string suffix = "/finish";
    return
    [
        .. GameEndpointCatalog.ByEndpointType.Values
            .Where(endpoint =>
            {
                if (!endpoint.Path.StartsWith(prefix, StringComparison.Ordinal)
                    || !endpoint.Path.EndsWith(suffix, StringComparison.Ordinal))
                {
                    return false;
                }

                var scenario = endpoint.Path[prefix.Length..^suffix.Length];
                return scenario.Length == 0
                    || scenario.Length > 1
                        && scenario[0] == '_'
                        && !scenario.AsSpan(1).Contains('/');
            })
            .OrderBy(endpoint => endpoint.Path, StringComparer.Ordinal)
    ];
}

static void AssertAnalyzerContracts(
    SmokeAnalyzerRegistry registry,
    IReadOnlyList<GameEndpointDescriptor> analysisEndpoints,
    IReadOnlyList<GameEndpointDescriptor> finishEndpoints)
{
    AssertEqual(3, registry.Registrations.Count, "SkillTips must use exactly three generic analyzer registrations.");

    var checkEvent = RequireRegistration<SingleModeCheckEventResponse>(
        registry,
        "/umamusume/single_mode*/check_event");
    var load = RequireRegistration<SingleModeLoadResponse>(
        registry,
        "/umamusume/single_mode*/load");
    var finish = RequireRegistration<SingleModeFinishResponse>(
        registry,
        "/umamusume/single_mode*/finish");

    var actualAnalysisPaths = checkEvent.Endpoints
        .Concat(load.Endpoints)
        .Select(endpoint => endpoint.Path)
        .Order(StringComparer.Ordinal)
        .ToArray();
    var expectedAnalysisPaths = analysisEndpoints
        .Select(endpoint => endpoint.Path)
        .Order(StringComparer.Ordinal)
        .ToArray();
    AssertTrue(
        expectedAnalysisPaths.SequenceEqual(actualAnalysisPaths),
        "CheckEvent/Load wildcard registrations must cover the complete current Host catalog exactly once.");

    var actualFinishPaths = finish.Endpoints
        .Select(endpoint => endpoint.Path)
        .Order(StringComparer.Ordinal)
        .ToArray();
    var expectedFinishPaths = finishEndpoints
        .Select(endpoint => endpoint.Path)
        .Order(StringComparer.Ordinal)
        .ToArray();
    AssertTrue(
        expectedFinishPaths.SequenceEqual(actualFinishPaths),
        "Finish wildcard registration must cover the complete current Host catalog.");
}

static SmokeAnalyzerRegistration RequireRegistration<TPayload>(
    SmokeAnalyzerRegistry registry,
    string wildcard)
{
    var registration = registry.Registrations.SingleOrDefault(entry => entry.PayloadType == typeof(TPayload))
        ?? throw new InvalidOperationException($"Register<{typeof(TPayload).Name}> was not observed.");
    AssertEqual(AnalyzerKind.Response, registration.Kind, $"Register<{typeof(TPayload).Name}> must be a response analyzer.");
    AssertEqual(0, registration.Priority, $"Register<{typeof(TPayload).Name}> must use the default priority.");
    AssertTrue(
        registration.Patterns is [{ Kind: EndpointPatternKind.Wildcard, Pattern: var pattern }]
        && string.Equals(pattern, wildcard, StringComparison.Ordinal),
        $"Register<{typeof(TPayload).Name}> must use wildcard {wildcard}.");
    return registration;
}

static async ValueTask AssertAnalysisEndpointDispatch(
    WorkspaceSmokeSession ui,
    IReadOnlyList<GameEndpointDescriptor> analysisEndpoints,
    GameEndpointDescriptor cleanupFinish)
{
    string? expectedResult = null;
    string? expectedWarning = null;
    foreach (var endpoint in analysisEndpoints)
    {
        var currentBefore = Workspace.Current;
        var screenBefore = ui.CaptureScreen();

        await DispatchHostResponse(
            endpoint.Path,
            CreateAnalysisResponse(endpoint, CreateCharaInfo(state: 1), []));
        AssertNoNewSkillTipsOutput(ui, currentBefore, screenBefore, $"{endpoint.Path} state=1");

        await DispatchHostResponse(
            endpoint.Path,
            CreateAnalysisResponse(endpoint, CreateCharaInfo(), [new SingleModeEventInfo()]));
        AssertNoNewSkillTipsOutput(ui, currentBefore, screenBefore, $"{endpoint.Path} with unchecked events");

        await DispatchHostResponse(
            endpoint.Path,
            CreateAnalysisResponse(endpoint, CreateCharaInfo(), []));
        var workspace = RequireSkillTipsWorkspace(ui);
        if (currentBefore is { Title: "SkillTipsResponseAnalyzer" })
        {
            AssertTrue(
                ReferenceEquals(currentBefore, workspace),
                $"{endpoint.Path} must reuse the canonical SkillTips workspace.");
        }
        AssertPanelContract(ui, workspace, endpoint.Path);
        var resultWithWarning = ui.CaptureScreen(320, 96);
        var warning = resultWithWarning.ReplaceLineEndings("\n")
            .Split('\n')
            .Last(line => line.Contains("WARN ", StringComparison.Ordinal));
        var result = ui.CaptureScreen(60, 96);
        if (expectedResult is null)
        {
            expectedResult = result;
            expectedWarning = warning;
        }
        else
        {
            AssertEqual(
                expectedResult,
                result,
                $"{endpoint.Path} must render the same complete result as every other CheckEvent/Load endpoint.");
            AssertEqual(
                expectedWarning,
                warning,
                $"{endpoint.Path} must preserve the same merged warning result.");
        }

        await DispatchHostResponse(cleanupFinish.Path, SerializeResponse(cleanupFinish));
        AssertNoSkillTipsWorkspace(ui, $"{endpoint.Path} cleanup must remove the completed training workspace.");
        AssertThrows<InvalidOperationException>(
            workspace.SwitchTo,
            $"{endpoint.Path} cleanup must tombstone the completed training workspace generation.");
    }
}

static async ValueTask AssertFinishWithoutWorkspaceDoesNotCreate(
    WorkspaceSmokeSession ui,
    IReadOnlyList<GameEndpointDescriptor> finishEndpoints)
{
    var currentBefore = Workspace.Current;
    var screenBefore = ui.CaptureScreen();
    foreach (var endpoint in finishEndpoints)
    {
        await DispatchHostResponse(endpoint.Path, SerializeResponse(endpoint));
        AssertNoSkillTipsWorkspace(
            ui,
            $"{endpoint.Path} must not create a workspace while cleaning up.");
        AssertNoNewSkillTipsOutput(ui, currentBefore, screenBefore, endpoint.Path);
    }
}

static async ValueTask AssertFinishWorkspaceLifecycle(
    WorkspaceSmokeSession ui,
    IReadOnlyList<GameEndpointDescriptor> finishEndpoints)
{
    var checkEvent = GameEndpointCatalog.ByEndpointType[typeof(GameApi.SingleMode.CheckEvent)];
    var foreignWorkspace = Workspace.Create("Unrelated workspace");
    foreach (var endpoint in finishEndpoints)
    {
        var existingWorkspace = RequireSkillTipsWorkspace(ui);

        await DispatchHostResponse(endpoint.Path, SerializeResponse(endpoint));
        AssertNoSkillTipsWorkspace(ui, $"{endpoint.Path} must remove the completed training workspace.");
        AssertTrue(
            ReferenceEquals(foreignWorkspace, Workspace.Create("UNRELATED WORKSPACE")),
            $"{endpoint.Path} must not remove an unrelated workspace.");
        AssertThrows<InvalidOperationException>(
            existingWorkspace.SwitchTo,
            $"{endpoint.Path} must tombstone the previous workspace generation.");

        await DispatchHostResponse(endpoint.Path, SerializeResponse(endpoint));
        AssertNoSkillTipsWorkspace(ui, $"Repeated {endpoint.Path} must not recreate the workspace.");

        await DispatchHostResponse(
            checkEvent.Path,
            MessagePackSerializer.Serialize(CreateCheckEventResponse()));
        var recreatedWorkspace = RequireSkillTipsWorkspace(ui);
        AssertTrue(
            !ReferenceEquals(existingWorkspace, recreatedWorkspace),
            $"{endpoint.Path} must create a new canonical workspace generation.");
        AssertTrue(
            ReferenceEquals(Workspace.Current, recreatedWorkspace),
            $"{endpoint.Path} next output must switch to the SkillTips workspace.");
        AssertPanelContract(ui, recreatedWorkspace, $"{endpoint.Path} next output");
    }

    foreignWorkspace.Remove();
}

static byte[] SerializeResponse(GameEndpointDescriptor endpoint)
    => MessagePackSerializer.Serialize(
        endpoint.ResponseType,
        Activator.CreateInstance(endpoint.ResponseType)
            ?? throw new InvalidOperationException($"{endpoint.ResponseType.FullName} could not be created."));

static byte[] CreateAnalysisResponse(
    GameEndpointDescriptor endpoint,
    SingleModeChara charaInfo,
    SingleModeEventInfo[] uncheckedEvents)
{
    var response = Activator.CreateInstance(endpoint.ResponseType)
        ?? throw new InvalidOperationException($"{endpoint.ResponseType.FullName} could not be created.");
    var dataField = endpoint.ResponseType.GetField("data", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
        ?? throw new InvalidOperationException($"{endpoint.ResponseType.FullName}.data was not found.");
    var data = Activator.CreateInstance(dataField.FieldType)
        ?? throw new InvalidOperationException($"{dataField.FieldType.FullName} could not be created.");

    if (endpoint.Path.EndsWith("/check_event", StringComparison.Ordinal))
    {
        RequirePublicField(dataField.FieldType, "chara_info", typeof(SingleModeChara))
            .SetValue(data, charaInfo);
        RequirePublicField(dataField.FieldType, "unchecked_event_array", typeof(SingleModeEventInfo[]))
            .SetValue(data, uncheckedEvents);
    }
    else if (endpoint.Path.EndsWith("/load", StringComparison.Ordinal))
    {
        RequirePublicField(dataField.FieldType, "single_mode_load_common", typeof(SingleModeLoadCommon))
            .SetValue(
                data,
                new SingleModeLoadCommon
                {
                    chara_info = charaInfo,
                    unchecked_event_array = uncheckedEvents
                });
    }
    else
    {
        throw new InvalidOperationException($"{endpoint.Path} is not a CheckEvent or Load endpoint.");
    }

    dataField.SetValue(response, data);
    return MessagePackSerializer.Serialize(endpoint.ResponseType, response);
}

static FieldInfo RequirePublicField(Type owner, string name, Type expectedType)
{
    var field = owner.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
        ?? throw new InvalidOperationException($"{owner.FullName}.{name} was not found.");
    AssertEqual(expectedType, field.FieldType, $"{owner.FullName}.{name} has an unexpected type.");
    return field;
}

static GameEndpointDescriptor ResolveRamenPacketEndpoint(byte[] payload)
{
    using var document = JsonDocument.Parse(MessagePackSerializer.ConvertToJson(payload));
    if (!document.RootElement.TryGetProperty("data", out var data)
        || data.ValueKind != JsonValueKind.Object)
    {
        throw new InvalidOperationException("Raw Ramen packet has no object-valued data field.");
    }

    var isCheckEvent = data.TryGetProperty("chara_info", out _)
        && data.TryGetProperty("unchecked_event_array", out _);
    var isLoad = data.TryGetProperty("single_mode_load_common", out var common)
        && common.ValueKind == JsonValueKind.Object
        && common.TryGetProperty("chara_info", out _)
        && common.TryGetProperty("unchecked_event_array", out _);
    return (isCheckEvent, isLoad) switch
    {
        (true, false) => GameEndpointCatalog.ByEndpointType[typeof(GameApi.SingleModeRamen.CheckEvent)],
        (false, true) => GameEndpointCatalog.ByEndpointType[typeof(GameApi.SingleModeRamen.Load)],
        _ => throw new InvalidOperationException(
            "Raw Ramen packet must have exactly one supported CheckEvent or Load schema.")
    };
}

static (SingleModeChara CharaInfo, SingleModeEventInfo[] UncheckedEvents) GetRamenAnalysisData(object response)
    => response switch
    {
        SingleModeRamenCheckEventResponse checkEvent =>
            (checkEvent.data.chara_info, checkEvent.data.unchecked_event_array),
        SingleModeRamenLoadResponse load =>
            (load.data.single_mode_load_common.chara_info, load.data.single_mode_load_common.unchecked_event_array),
        _ => throw new InvalidOperationException(
            $"Unexpected raw Ramen response type: {response.GetType().FullName}.")
    };

static async ValueTask DispatchHostResponse(string canonicalUrl, byte[] payload)
    => await SmokeAnalyzerRegistry.Active.DispatchResponse(canonicalUrl, payload);

static void InitializeHostConfigForSmoke()
    => UmamusumeResponseAnalyzer.Config.Initialize();

static async Task InitializeSmokeDatabase(IEnumerable<UraSkillData> skills)
{
    WriteBrotliJson(Database.EVENT_NAME_FILEPATH, new List<Story>());
    WriteBrotliJson(Database.NAMES_FILEPATH, new List<BaseName>());
    WriteBrotliJson(Database.SKILLS_FILEPATH, skills.ToList());
    WriteBrotliJson(Database.SKILL_UPGRADE_SPECIALITY_FILEPATH, new List<SkillUpgradeSpeciality>());
    WriteBrotliJson(Database.TALENT_SKILLS_FILEPATH, new Dictionary<int, TalentSkillData[]>());
    WriteBrotliJson(Database.FACTOR_IDS_FILEPATH, new Dictionary<int, string>());
    WriteBrotliJson(Database.SADDLE_IDS_FILEPATH, Array.Empty<int>());
    WriteBrotliJson(Database.SUCCESSION_RELATION_FILEPATH, new SuccessionRelationTable());
    if (await Database.Initialize() != DatabaseAvailability.Ready)
        throw new InvalidOperationException("SkillTips smoke database fixture did not load atomically.");
}

static void WriteBrotliJson<T>(string path, T value)
{
    using var file = File.Create(path);
    using var brotli = new BrotliStream(file, CompressionLevel.SmallestSize);
    using var writer = new StreamWriter(brotli, Encoding.UTF8);
    using var json = new JsonTextWriter(writer);
    Newtonsoft.Json.JsonSerializer.CreateDefault().Serialize(json, value);
}

static Workspace RequireSkillTipsWorkspace(WorkspaceSmokeSession ui)
{
    var workspace = Workspace.Current;
    AssertTrue(
        workspace is not null
        && string.Equals(workspace.Title, "SkillTipsResponseAnalyzer", StringComparison.OrdinalIgnoreCase)
        && ui.CaptureScreen().Contains(I18N_RecommendedSkills, StringComparison.Ordinal),
        "The visible SkillTipsResponseAnalyzer workspace was not found.");
    return workspace!;
}

static void AssertNoSkillTipsWorkspace(WorkspaceSmokeSession ui, string message)
{
    var screen = ui.CaptureScreen();
    AssertTrue(
        !string.Equals(Workspace.Current?.Title, "SkillTipsResponseAnalyzer", StringComparison.OrdinalIgnoreCase)
        && !screen.Contains(I18N_RecommendedSkills, StringComparison.Ordinal),
        message);
}

static void AssertNoNewSkillTipsOutput(
    WorkspaceSmokeSession ui,
    Workspace? currentBefore,
    string screenBefore,
    string operation)
{
    AssertTrue(
        ReferenceEquals(Workspace.Current, currentBefore)
        && string.Equals(ui.CaptureScreen(), screenBefore, StringComparison.Ordinal),
        $"{operation} must not change the visible workspace, panel, or notification state.");
}

static void AssertThrows<TException>(Action action, string message)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException(message);
}

static void AssertTrue(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static void AssertEqual<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{message} Expected={expected}, Actual={actual}");
}

sealed class SmokePluginContext : IPluginContext
{
    public SmokePluginContext(IApplication application)
    {
        Application = application;
        AnalyzerRegistry = new();
    }

    public IApplication Application { get; }
    public IPluginHostEvents Events { get; } = new ThrowingPluginHostEvents();
    public SmokeAnalyzerRegistry AnalyzerRegistry { get; }
    public IPluginAnalyzerRegistry Analyzers => AnalyzerRegistry;
    public bool IsPluginAvailable(string internalName) => false;

    public void RunBackground(Func<CancellationToken, ValueTask> operation)
        => throw new NotSupportedException("SkillTips smoke does not use background operations.");
}

sealed class ThrowingPluginHostEvents : IPluginHostEvents
{
    public void OnStarted(Func<CancellationToken, ValueTask> handler)
        => throw new NotSupportedException("SkillTips smoke does not use host events.");
}

sealed class SmokeAnalyzerRegistry : IPluginAnalyzerRegistry
{
    static readonly GameHttpHeaders EmptyHeaders = new(null, null, null, null, null, null);
    readonly List<SmokeAnalyzerRegistration> registrations = [];

    public SmokeAnalyzerRegistry() => Active = this;

    public static SmokeAnalyzerRegistry Active { get; private set; } = null!;
    public IReadOnlyList<SmokeAnalyzerRegistration> Registrations => registrations;

    public void Register<TPayload>(
        AnalyzerKind kind,
        IReadOnlyList<EndpointPattern> patterns,
        Func<AnalyzerInvocation<TPayload>, ValueTask> handler,
        int priority = 0)
    {
        var endpoints = GameEndpointCatalog.ByPath.Values
            .Where(endpoint => patterns.Any(pattern => Matches(pattern, endpoint.Path)))
            .DistinctBy(endpoint => endpoint.Path)
            .OrderBy(endpoint => endpoint.Path, StringComparer.Ordinal)
            .ToArray();
        if (endpoints.Length == 0)
            throw new InvalidOperationException($"Register<{typeof(TPayload).Name}> matched no current Host endpoints.");

        registrations.Add(new(
            typeof(TPayload),
            kind,
            [.. patterns],
            priority,
            endpoints,
            async (endpoint, payload, headers) =>
            {
                var projected = typeof(TPayload) == typeof(ReadOnlyMemory<byte>)
                    ? (TPayload)(object)new ReadOnlyMemory<byte>(payload)
                    : MessagePackSerializer.Deserialize<TPayload>(payload);
                await handler(new(endpoint, projected, headers));
            }));
    }

    public async ValueTask DispatchResponse(string canonicalUrl, byte[] payload)
    {
        if (!GameEndpointCatalog.ByPath.TryGetValue(canonicalUrl, out var endpoint))
            throw new InvalidOperationException($"Unknown endpoint: {canonicalUrl}");

        var matches = registrations
            .Where(registration => registration.Kind == AnalyzerKind.Response
                && registration.Endpoints.Any(candidate => candidate.Path == canonicalUrl))
            .OrderByDescending(registration => registration.Priority)
            .ToArray();
        if (matches.Length == 0)
            throw new InvalidOperationException($"No response analyzer matched {canonicalUrl}.");

        foreach (var registration in matches)
            await registration.Dispatch(endpoint, payload, EmptyHeaders);
    }

    public void Clear() => registrations.Clear();

    static bool Matches(EndpointPattern pattern, string path)
        => pattern.Kind switch
        {
            EndpointPatternKind.Exact => string.Equals(pattern.Pattern, path, StringComparison.Ordinal),
            EndpointPatternKind.Wildcard => Regex.IsMatch(
                path,
                $"\\A{Regex.Escape(pattern.Pattern).Replace("\\*", "[^/]*", StringComparison.Ordinal)}\\z",
                RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
                TimeSpan.FromMilliseconds(100)),
            EndpointPatternKind.Regex => Regex.IsMatch(
                path,
                $"\\A(?:{pattern.Pattern})\\z",
                RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
                TimeSpan.FromMilliseconds(100)),
            _ => throw new ArgumentOutOfRangeException(nameof(pattern))
        };
}

sealed record SmokeAnalyzerRegistration(
    Type PayloadType,
    AnalyzerKind Kind,
    IReadOnlyList<EndpointPattern> Patterns,
    int Priority,
    IReadOnlyList<GameEndpointDescriptor> Endpoints,
    Func<GameEndpointDescriptor, byte[], GameHttpHeaders, ValueTask> Dispatch);
