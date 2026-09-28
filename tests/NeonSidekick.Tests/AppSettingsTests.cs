using System.Text.Json;
using NeonSidekick.App;
using NeonSidekick.Files;
using NeonSidekick.Settings;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

public class AppSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// Every field set to a non-default value. If a field is added to AppSettingsData but not to
    /// AppSettings.Copy, this object stops surviving Update → Current and the test fails.
    /// </summary>
    private static AppSettingsData FullyNonDefault() => new()
    {
        SchemaVersion = 7,
        CommandTypoIntercept = false,
        KeepCommandHistory = false,
        CopyUserPrompt = false,
        DraftEditor = "code --wait",
        HideExitAutocomplete = false,
        ImageThumbnailSize = "large",
        Theme = "noir",
        BotChatLlmMode = "multi",
        LlmMidTurnUsage = "estimate",   // last-known is the default since later on 2026-09-25
        BotChatImages = true,
        BotChatImageMode = "autonomous",
        BotChatTxt2ImgWorkflow = "flux",
        BotChatImg2ImgWorkflow = "flux-edit",
        BotChatImg2ImgMode = "chat-history",
        BotChatImageAsync = false,
        BotChatNonTtsDelaySeconds = 12,
        BotChatSkills = true,
        BotChatVision = true,
        Memory = false,
        NewProfileMode = "advanced",
        PastePreviewLines = 7,
        QueueCancelMode = "drain",
        QueueMessages = false,
        ShowImageThumbnails = false,
        TranscriptMarkdown = false,
        WelcomeSplashMode = "tiled",
        ShowWorkingDirectory = false,
        ShowToolbar = false,
        ThemedViewer = false,
        WorkingDirectory = @"D:\elsewhere\files",
        LlmApiKey = "sk-test",
        LlmAutoCompactPercent = 65,
        LlmMaxTurns = 40,
        LlmCompactKeepRecent = 4,
        LlmCompactShowSummary = true,
        GitNativeEmail = "me@example.invalid",
        GitNativeName = "Some User",
        ShellToolBridge = true,
        ShellPoliceOutsidePaths = false,
        ShellPreferNative = false,
        LlmCompactType = "prune",
        LlmContextLength = 32_768,
        LlmMaxToolIterations = 22,
        LlmModel = "qwen3",
        LlmReasoning = "high",
        LlmRequestTimeoutSeconds = 12.5,
        LlmScanMode = "both",
        LlmToolCompactType = "stop",
        LlmOfferTools = false,
        LlmTurnTimeoutSeconds = 40,
        LlmUrl = "http://box:8000/v1",
        LlmUseFunVerbs = true,
        LlmShowThinking = false,
        TtsHttpUrl = "http://box:8880/v1",
        TtsOutput = true,
        TtsSource = "http",
        TtsSpeed = 1.4,
        TtsVoice = "bm_george",
        TtsVoice2 = "af_sky",
        TtsVoiceMix = 70,
        TtsVoicePreview = false,
        SttInput = true,
        SttInterrupt = true,
        SttInterruptConfirmMs = 600,
        SttInterruptEchoGuard = 80,
        SttPushToTalkKey = "F9",
        SttVoskModel = "vosk-model-en-us-0.22-lgraph",
        SttWake = true,
        SttWakePhrase = "computer",
        SttWhisperModel = "ggml-small.en.bin",
        ToolsDisabled = ["read_file", "web_search"],
        ToolsDollarMention = false,
        AskMaxChoices = 15,
        AskMaxQuestions = 3,
        AskUser = false,
        FileSafeEdits = true,
        FileTools = false,
        FileMentionFolderMode = "folder-apply",
        FileTreeMaxLength = 750,
        FileTreeShowSizes = false,
        FileViewImageMaxPerCall = 25,
        WebSearxngUrl = "http://localhost:8080",
        WebBrowserMode = "chromium",
        WebBrowserNetworkMode = "both",
        WebBrowserPath = @"C:\tools\chrome.exe",
        WebSearchMaxResults = 5,
        WebSearchMethod = "searxng",
        WebTools = false,
        AgentSkills = false,
        ExternalSkills = true,
        ProjectFile = false,
        ReflectionAutoLearn = false,
        ReflectionCooldownMinutes = 90,
        ReflectionCooldownMode = "all-skills",
        ReflectionIncludesSessions = false,
        ReflectionMaxRequests = 9,
        ReflectionMinToolCalls = 7,
        ReflectionReasoning = "high",
        ReflectionWindow = 5,
        ReflectionYieldsToTurns = false,
        ReflectionEditsSupportingFiles = true,
        SkillCompactMode = "unprotected",
        SkillHashMention = false,
        SessionLogging = false,
        SessionNamingMode = "first-line",
        SessionRetentionDays = 45,
        SessionSearchMaxResults = 3,
        SessionShowName = "none",
        SessionTool = false,
        McpConnectTimeoutSeconds = 45,
        McpServers = true,
        McpServersDisabled = ["docker"],
    };

    private static void AssertSame(AppSettingsData expected, AppSettingsData actual)
    {
        Assert.Equal(expected.SchemaVersion, actual.SchemaVersion);
        Assert.Equal(expected.CommandTypoIntercept, actual.CommandTypoIntercept);
        Assert.Equal(expected.KeepCommandHistory, actual.KeepCommandHistory);
        Assert.Equal(expected.CopyUserPrompt, actual.CopyUserPrompt);
        Assert.Equal(expected.DraftEditor, actual.DraftEditor);
        Assert.Equal(expected.HideExitAutocomplete, actual.HideExitAutocomplete);
        Assert.Equal(expected.ImageThumbnailSize, actual.ImageThumbnailSize);
        Assert.Equal(expected.Theme, actual.Theme);
        Assert.Equal(expected.Memory, actual.Memory);
        Assert.Equal(expected.NewProfileMode, actual.NewProfileMode);
        Assert.Equal(expected.PastePreviewLines, actual.PastePreviewLines);
        Assert.Equal(expected.QueueCancelMode, actual.QueueCancelMode);
        Assert.Equal(expected.QueueMessages, actual.QueueMessages);
        Assert.Equal(expected.ShowImageThumbnails, actual.ShowImageThumbnails);
        Assert.Equal(expected.TranscriptMarkdown, actual.TranscriptMarkdown);
        Assert.Equal(expected.WelcomeSplashMode, actual.WelcomeSplashMode);
        Assert.Equal(expected.ShowWorkingDirectory, actual.ShowWorkingDirectory);
        Assert.Equal(expected.ShowToolbar, actual.ShowToolbar);
        Assert.Equal(expected.ThemedViewer, actual.ThemedViewer);
        Assert.Equal(expected.WorkingDirectory, actual.WorkingDirectory);
        Assert.Equal(expected.LlmApiKey, actual.LlmApiKey);
        Assert.Equal(expected.LlmAutoCompactPercent, actual.LlmAutoCompactPercent);
        Assert.Equal(expected.LlmMaxTurns, actual.LlmMaxTurns);
        Assert.Equal(expected.LlmCompactKeepRecent, actual.LlmCompactKeepRecent);
        Assert.Equal(expected.LlmCompactShowSummary, actual.LlmCompactShowSummary);
        Assert.Equal(expected.GitNativeEmail, actual.GitNativeEmail);
        Assert.Equal(expected.GitNativeName, actual.GitNativeName);
        Assert.Equal(expected.ShellToolBridge, actual.ShellToolBridge);
        Assert.Equal(expected.ShellPoliceOutsidePaths, actual.ShellPoliceOutsidePaths);
        Assert.Equal(expected.ShellPreferNative, actual.ShellPreferNative);
        Assert.Equal(expected.LlmCompactType, actual.LlmCompactType);
        Assert.Equal(expected.LlmContextLength, actual.LlmContextLength);
        Assert.Equal(expected.LlmMaxToolIterations, actual.LlmMaxToolIterations);
        Assert.Equal(expected.LlmModel, actual.LlmModel);
        Assert.Equal(expected.LlmReasoning, actual.LlmReasoning);
        Assert.Equal(expected.LlmRequestTimeoutSeconds, actual.LlmRequestTimeoutSeconds);
        Assert.Equal(expected.LlmScanMode, actual.LlmScanMode);
        Assert.Equal(expected.LlmToolCompactType, actual.LlmToolCompactType);
        Assert.Equal(expected.LlmOfferTools, actual.LlmOfferTools);
        Assert.Equal(expected.LlmTurnTimeoutSeconds, actual.LlmTurnTimeoutSeconds);
        Assert.Equal(expected.LlmUrl, actual.LlmUrl);
        Assert.Equal(expected.LlmUseFunVerbs, actual.LlmUseFunVerbs);
        Assert.Equal(expected.LlmShowThinking, actual.LlmShowThinking);
        Assert.Equal(expected.TtsHttpUrl, actual.TtsHttpUrl);
        Assert.Equal(expected.TtsOutput, actual.TtsOutput);
        Assert.Equal(expected.TtsSource, actual.TtsSource);
        Assert.Equal(expected.TtsSpeed, actual.TtsSpeed);
        Assert.Equal(expected.TtsVoice, actual.TtsVoice);
        Assert.Equal(expected.TtsVoice2, actual.TtsVoice2);
        Assert.Equal(expected.TtsVoiceMix, actual.TtsVoiceMix);
        Assert.Equal(expected.TtsVoicePreview, actual.TtsVoicePreview);
        Assert.Equal(expected.SttInput, actual.SttInput);
        Assert.Equal(expected.SttInterrupt, actual.SttInterrupt);
        Assert.Equal(expected.SttInterruptConfirmMs, actual.SttInterruptConfirmMs);
        Assert.Equal(expected.SttInterruptEchoGuard, actual.SttInterruptEchoGuard);
        Assert.Equal(expected.SttPushToTalkKey, actual.SttPushToTalkKey);
        Assert.Equal(expected.SttVoskModel, actual.SttVoskModel);
        Assert.Equal(expected.SttWake, actual.SttWake);
        Assert.Equal(expected.SttWakePhrase, actual.SttWakePhrase);
        Assert.Equal(expected.SttWhisperModel, actual.SttWhisperModel);
        Assert.Equal(expected.ToolsDisabled, actual.ToolsDisabled);
        Assert.Equal(expected.ToolsDollarMention, actual.ToolsDollarMention);
        Assert.Equal(expected.McpConnectTimeoutSeconds, actual.McpConnectTimeoutSeconds);
        Assert.Equal(expected.McpServers, actual.McpServers);
        Assert.Equal(expected.McpServersDisabled, actual.McpServersDisabled);
        Assert.Equal(expected.AskMaxChoices, actual.AskMaxChoices);
        Assert.Equal(expected.AskMaxQuestions, actual.AskMaxQuestions);
        Assert.Equal(expected.AskUser, actual.AskUser);
        Assert.Equal(expected.FileSafeEdits, actual.FileSafeEdits);
        Assert.Equal(expected.FileTools, actual.FileTools);
        Assert.Equal(expected.FileMentionFolderMode, actual.FileMentionFolderMode);
        Assert.Equal(expected.FileTreeMaxLength, actual.FileTreeMaxLength);
        Assert.Equal(expected.FileTreeShowSizes, actual.FileTreeShowSizes);
        Assert.Equal(expected.FileViewImageMaxPerCall, actual.FileViewImageMaxPerCall);
        Assert.Equal(expected.WebSearxngUrl, actual.WebSearxngUrl);
        Assert.Equal(expected.WebBrowserMode, actual.WebBrowserMode);
        Assert.Equal(expected.WebBrowserNetworkMode, actual.WebBrowserNetworkMode);
        Assert.Equal(expected.WebBrowserPath, actual.WebBrowserPath);
        Assert.Equal(expected.WebSearchMaxResults, actual.WebSearchMaxResults);
        Assert.Equal(expected.WebSearchMethod, actual.WebSearchMethod);
        Assert.Equal(expected.WebTools, actual.WebTools);
        Assert.Equal(expected.AgentSkills, actual.AgentSkills);
        Assert.Equal(expected.ExternalSkills, actual.ExternalSkills);
        Assert.Equal(expected.ProjectFile, actual.ProjectFile);
        Assert.Equal(expected.ReflectionAutoLearn, actual.ReflectionAutoLearn);
        Assert.Equal(expected.ReflectionCooldownMinutes, actual.ReflectionCooldownMinutes);
        Assert.Equal(expected.ReflectionCooldownMode, actual.ReflectionCooldownMode);
        Assert.Equal(expected.ReflectionIncludesSessions, actual.ReflectionIncludesSessions);
        Assert.Equal(expected.ReflectionMaxRequests, actual.ReflectionMaxRequests);
        Assert.Equal(expected.ReflectionMinToolCalls, actual.ReflectionMinToolCalls);
        Assert.Equal(expected.ReflectionReasoning, actual.ReflectionReasoning);
        Assert.Equal(expected.ReflectionWindow, actual.ReflectionWindow);
        Assert.Equal(expected.ReflectionYieldsToTurns, actual.ReflectionYieldsToTurns);
        Assert.Equal(expected.ReflectionEditsSupportingFiles, actual.ReflectionEditsSupportingFiles);
        Assert.Equal(expected.SkillCompactMode, actual.SkillCompactMode);
        Assert.Equal(expected.SkillHashMention, actual.SkillHashMention);
        Assert.Equal(expected.SessionLogging, actual.SessionLogging);
        Assert.Equal(expected.SessionNamingMode, actual.SessionNamingMode);
        Assert.Equal(expected.SessionRetentionDays, actual.SessionRetentionDays);
        Assert.Equal(expected.SessionSearchMaxResults, actual.SessionSearchMaxResults);
        Assert.Equal(expected.SessionShowName, actual.SessionShowName);
        Assert.Equal(expected.SessionTool, actual.SessionTool);
    }

    [Fact]
    public void EveryField_SurvivesCopy()
    {
        var full = FullyNonDefault();
        AssertSame(full, AppSettings.Copy(full));
    }

    [Fact]
    public void EveryField_SurvivesUpdateAndCurrent()
    {
        using var settings = new AppSettings(_dir);
        var full = FullyNonDefault();
        settings.Update(d =>
        {
            d.SchemaVersion = full.SchemaVersion;
            d.CommandTypoIntercept = full.CommandTypoIntercept;
            d.KeepCommandHistory = full.KeepCommandHistory;
            d.CopyUserPrompt = full.CopyUserPrompt;
            d.DraftEditor = full.DraftEditor;
            d.HideExitAutocomplete = full.HideExitAutocomplete;
            d.ImageThumbnailSize = full.ImageThumbnailSize;
            d.Theme = full.Theme;
            d.Memory = full.Memory;
            d.NewProfileMode = full.NewProfileMode;
            d.PastePreviewLines = full.PastePreviewLines;
            d.QueueCancelMode = full.QueueCancelMode;
            d.QueueMessages = full.QueueMessages;
            d.ShowImageThumbnails = full.ShowImageThumbnails;
            d.TranscriptMarkdown = full.TranscriptMarkdown;
            d.WelcomeSplashMode = full.WelcomeSplashMode;
            d.ShowWorkingDirectory = full.ShowWorkingDirectory;
            d.ShowToolbar = full.ShowToolbar;
            d.ThemedViewer = full.ThemedViewer;
            d.WorkingDirectory = full.WorkingDirectory;
            d.LlmApiKey = full.LlmApiKey;
            d.LlmAutoCompactPercent = full.LlmAutoCompactPercent;
            d.LlmMaxTurns = full.LlmMaxTurns;
            d.LlmCompactKeepRecent = full.LlmCompactKeepRecent;
            d.LlmCompactShowSummary = full.LlmCompactShowSummary;
            d.GitNativeEmail = full.GitNativeEmail;
            d.GitNativeName = full.GitNativeName;
            d.ShellToolBridge = full.ShellToolBridge;
            d.ShellPoliceOutsidePaths = full.ShellPoliceOutsidePaths;
            d.ShellPreferNative = full.ShellPreferNative;
            d.LlmCompactType = full.LlmCompactType;
            d.LlmContextLength = full.LlmContextLength;
            d.LlmMaxToolIterations = full.LlmMaxToolIterations;
            d.LlmModel = full.LlmModel;
            d.LlmReasoning = full.LlmReasoning;
            d.LlmRequestTimeoutSeconds = full.LlmRequestTimeoutSeconds;
            d.LlmScanMode = full.LlmScanMode;
            d.LlmToolCompactType = full.LlmToolCompactType;
            d.LlmOfferTools = full.LlmOfferTools;
            d.LlmTurnTimeoutSeconds = full.LlmTurnTimeoutSeconds;
            d.LlmUrl = full.LlmUrl;
            d.LlmUseFunVerbs = full.LlmUseFunVerbs; d.LlmShowThinking = full.LlmShowThinking;
            d.TtsHttpUrl = full.TtsHttpUrl;
            d.TtsOutput = full.TtsOutput;
            d.TtsSource = full.TtsSource;
            d.TtsSpeed = full.TtsSpeed;
            d.TtsVoice = full.TtsVoice;
            d.TtsVoice2 = full.TtsVoice2;
            d.TtsVoiceMix = full.TtsVoiceMix;
            d.TtsVoicePreview = full.TtsVoicePreview;
            d.SttInput = full.SttInput;
            d.SttInterrupt = full.SttInterrupt;
            d.SttInterruptConfirmMs = full.SttInterruptConfirmMs;
            d.SttInterruptEchoGuard = full.SttInterruptEchoGuard;
            d.SttPushToTalkKey = full.SttPushToTalkKey;
            d.SttVoskModel = full.SttVoskModel;
            d.SttWake = full.SttWake;
            d.SttWakePhrase = full.SttWakePhrase;
            d.SttWhisperModel = full.SttWhisperModel;
            d.ToolsDisabled = full.ToolsDisabled;
            d.ToolsDollarMention = full.ToolsDollarMention;
            d.McpConnectTimeoutSeconds = full.McpConnectTimeoutSeconds;
            d.McpServers = full.McpServers;
            d.McpServersDisabled = full.McpServersDisabled;
            d.AskMaxChoices = full.AskMaxChoices;
            d.AskMaxQuestions = full.AskMaxQuestions;
            d.AskUser = full.AskUser;
            d.FileSafeEdits = full.FileSafeEdits;
            d.FileTools = full.FileTools;
            d.FileMentionFolderMode = full.FileMentionFolderMode;
            d.FileTreeMaxLength = full.FileTreeMaxLength;
            d.FileTreeShowSizes = full.FileTreeShowSizes;
            d.FileViewImageMaxPerCall = full.FileViewImageMaxPerCall;
            d.WebSearxngUrl = full.WebSearxngUrl;
            d.WebBrowserMode = full.WebBrowserMode;
            d.WebBrowserNetworkMode = full.WebBrowserNetworkMode;
            d.WebBrowserPath = full.WebBrowserPath;
            d.WebSearchMaxResults = full.WebSearchMaxResults;
            d.WebSearchMethod = full.WebSearchMethod;
            d.WebTools = full.WebTools;
            d.AgentSkills = full.AgentSkills;
            d.ExternalSkills = full.ExternalSkills;
            d.ProjectFile = full.ProjectFile;
            d.ReflectionAutoLearn = full.ReflectionAutoLearn;
            d.ReflectionCooldownMinutes = full.ReflectionCooldownMinutes;
            d.ReflectionCooldownMode = full.ReflectionCooldownMode;
            d.ReflectionIncludesSessions = full.ReflectionIncludesSessions;
            d.ReflectionMaxRequests = full.ReflectionMaxRequests;
            d.ReflectionMinToolCalls = full.ReflectionMinToolCalls;
            d.ReflectionReasoning = full.ReflectionReasoning;
            d.ReflectionWindow = full.ReflectionWindow;
            d.ReflectionYieldsToTurns = full.ReflectionYieldsToTurns;
            d.ReflectionEditsSupportingFiles = full.ReflectionEditsSupportingFiles;
            d.SkillCompactMode = full.SkillCompactMode;
            d.SkillHashMention = full.SkillHashMention;
            d.SessionLogging = full.SessionLogging;
            d.SessionNamingMode = full.SessionNamingMode;
            d.SessionRetentionDays = full.SessionRetentionDays;
            d.SessionSearchMaxResults = full.SessionSearchMaxResults;
            d.SessionShowName = full.SessionShowName;
            d.SessionTool = full.SessionTool;
        });

        AssertSame(full, settings.Current);
    }

    [Fact]
    public async Task EveryField_SurvivesARoundTripThroughTheFile()
    {
        var full = FullyNonDefault();
        using (var settings = new AppSettings(_dir))
        {
            settings.Update(d =>
            {
                d.SchemaVersion = full.SchemaVersion;
                d.CommandTypoIntercept = full.CommandTypoIntercept;
                d.KeepCommandHistory = full.KeepCommandHistory;
                d.CopyUserPrompt = full.CopyUserPrompt;
                d.DraftEditor = full.DraftEditor;
                d.HideExitAutocomplete = full.HideExitAutocomplete;
                d.ImageThumbnailSize = full.ImageThumbnailSize;
                d.Theme = full.Theme;
                d.Memory = full.Memory;
                d.NewProfileMode = full.NewProfileMode;
                d.PastePreviewLines = full.PastePreviewLines;
                d.QueueCancelMode = full.QueueCancelMode;
                d.QueueMessages = full.QueueMessages;
                d.ShowImageThumbnails = full.ShowImageThumbnails;
                d.TranscriptMarkdown = full.TranscriptMarkdown;
                d.WelcomeSplashMode = full.WelcomeSplashMode;
                d.ShowWorkingDirectory = full.ShowWorkingDirectory;
                d.ShowToolbar = full.ShowToolbar;
                d.ThemedViewer = full.ThemedViewer;
                d.WorkingDirectory = full.WorkingDirectory;
                d.LlmApiKey = full.LlmApiKey;
                d.LlmAutoCompactPercent = full.LlmAutoCompactPercent;
                d.LlmMaxTurns = full.LlmMaxTurns;
                d.LlmCompactKeepRecent = full.LlmCompactKeepRecent;
                d.LlmCompactShowSummary = full.LlmCompactShowSummary;
                d.GitNativeEmail = full.GitNativeEmail;
                d.GitNativeName = full.GitNativeName;
                d.ShellToolBridge = full.ShellToolBridge;
                d.ShellPoliceOutsidePaths = full.ShellPoliceOutsidePaths;
                d.ShellPreferNative = full.ShellPreferNative;
                d.LlmCompactType = full.LlmCompactType;
                d.LlmContextLength = full.LlmContextLength;
                d.LlmMaxToolIterations = full.LlmMaxToolIterations;
                d.LlmModel = full.LlmModel;
                d.LlmReasoning = full.LlmReasoning;
                d.LlmRequestTimeoutSeconds = full.LlmRequestTimeoutSeconds;
                d.LlmScanMode = full.LlmScanMode;
                d.LlmToolCompactType = full.LlmToolCompactType;
                d.LlmOfferTools = full.LlmOfferTools;
                d.LlmTurnTimeoutSeconds = full.LlmTurnTimeoutSeconds;
                d.LlmUrl = full.LlmUrl;
                d.LlmUseFunVerbs = full.LlmUseFunVerbs; d.LlmShowThinking = full.LlmShowThinking;
                d.TtsHttpUrl = full.TtsHttpUrl;
                d.TtsOutput = full.TtsOutput;
                d.TtsSource = full.TtsSource;
                d.TtsSpeed = full.TtsSpeed;
                d.TtsVoice = full.TtsVoice;
                d.TtsVoice2 = full.TtsVoice2;
                d.TtsVoiceMix = full.TtsVoiceMix;
                d.TtsVoicePreview = full.TtsVoicePreview;
                d.SttInput = full.SttInput;
                d.SttInterrupt = full.SttInterrupt;
                d.SttInterruptConfirmMs = full.SttInterruptConfirmMs;
                d.SttInterruptEchoGuard = full.SttInterruptEchoGuard;
                d.SttPushToTalkKey = full.SttPushToTalkKey;
                d.SttVoskModel = full.SttVoskModel;
                d.SttWake = full.SttWake;
                d.SttWakePhrase = full.SttWakePhrase;
                d.SttWhisperModel = full.SttWhisperModel;
                d.ToolsDisabled = full.ToolsDisabled;
                d.ToolsDollarMention = full.ToolsDollarMention;
                d.McpConnectTimeoutSeconds = full.McpConnectTimeoutSeconds;
                d.McpServers = full.McpServers;
                d.McpServersDisabled = full.McpServersDisabled;
                d.AskMaxChoices = full.AskMaxChoices;
                d.AskMaxQuestions = full.AskMaxQuestions;
                d.AskUser = full.AskUser;
                    d.FileSafeEdits = full.FileSafeEdits;
                d.FileTools = full.FileTools;
                d.FileMentionFolderMode = full.FileMentionFolderMode;
                d.FileTreeMaxLength = full.FileTreeMaxLength;
                d.FileTreeShowSizes = full.FileTreeShowSizes;
                d.FileViewImageMaxPerCall = full.FileViewImageMaxPerCall;
                d.WebSearxngUrl = full.WebSearxngUrl;
                d.WebBrowserMode = full.WebBrowserMode;
                d.WebBrowserNetworkMode = full.WebBrowserNetworkMode;
                d.WebBrowserPath = full.WebBrowserPath;
                d.WebSearchMaxResults = full.WebSearchMaxResults;
                d.WebSearchMethod = full.WebSearchMethod;
                d.WebTools = full.WebTools;
                d.AgentSkills = full.AgentSkills;
                d.ExternalSkills = full.ExternalSkills;
                d.ProjectFile = full.ProjectFile;
                d.ReflectionAutoLearn = full.ReflectionAutoLearn;
                d.ReflectionCooldownMinutes = full.ReflectionCooldownMinutes;
                d.ReflectionCooldownMode = full.ReflectionCooldownMode;
                d.ReflectionIncludesSessions = full.ReflectionIncludesSessions;
                d.ReflectionMaxRequests = full.ReflectionMaxRequests;
                d.ReflectionMinToolCalls = full.ReflectionMinToolCalls;
                d.ReflectionReasoning = full.ReflectionReasoning;
                d.ReflectionWindow = full.ReflectionWindow;
                d.ReflectionYieldsToTurns = full.ReflectionYieldsToTurns;
                d.ReflectionEditsSupportingFiles = full.ReflectionEditsSupportingFiles;
                d.SkillCompactMode = full.SkillCompactMode;
                d.SkillHashMention = full.SkillHashMention;
                d.SessionLogging = full.SessionLogging;
                d.SessionNamingMode = full.SessionNamingMode;
                d.SessionRetentionDays = full.SessionRetentionDays;
                d.SessionSearchMaxResults = full.SessionSearchMaxResults;
                d.SessionShowName = full.SessionShowName;
                d.SessionTool = full.SessionTool;
            });
            await settings.FlushAsync();
        }

        Assert.True(File.Exists(Profiles.ProfileFile(_dir, Profiles.DefaultName)));
        using var reloaded = new AppSettings(_dir);
        AssertSame(full, reloaded.Current);
        Assert.Equal(Profiles.ProfileFile(_dir, Profiles.DefaultName), reloaded.FilePath);
        Assert.Equal(Profiles.Directory(_dir, Profiles.DefaultName), reloaded.ProfileDirectory);
    }

    /// <summary><c>SQL connections offered</c> (later on 2026-09-23): not narrowed (null), none ([]) and a list each survive a save and a reload as themselves.</summary>
    [Fact]
    public async Task SqlConnectionsOffered_KeepsNullEmptyAndAList_ApartAcrossAReload()
    {
        foreach (var offered in new List<string>?[] { null, [], ["aw", "prod"] })
        {
            using (var settings = new AppSettings(_dir))
            {
                settings.Update(d => d.SqlConnectionsOffered = offered is null ? null : [.. offered]);
                await settings.FlushAsync();
            }

            using var reloaded = new AppSettings(_dir);
            Assert.Equal(offered, reloaded.Current.SqlConnectionsOffered);
        }

        Assert.Null(new AppSettingsData().SqlConnectionsOffered);   // every profile starts not narrowed
    }

    [Fact]
    public void Current_IsASnapshot_NotTheLiveObject()
    {
        using var settings = new AppSettings(_dir);
        var a = settings.Current;
        a.LlmModel = "mutated-locally";
        a.ToolsDisabled.Add("read_file");
        Assert.Equal("", settings.Current.LlmModel);
        Assert.Equal(["git_delete", "unzip", "zip"], settings.Current.ToolsDisabled);   // the default: git_delete since 2026-09-20, zip and unzip since 2026-09-21 (delete was here until later that day, git_discard until 2026-09-23); the local Add never reached the store
    }

    [Fact]
    public void Copy_DeepCopiesTheDisabledToolsList()
    {
        var full = FullyNonDefault();
        var copy = AppSettings.Copy(full);
        copy.ToolsDisabled.Add("zip");
        Assert.Equal(["read_file", "web_search"], full.ToolsDisabled);
        copy.McpServersDisabled.Add("chrome");
        Assert.Equal(["docker"], full.McpServersDisabled);   // the second list (2026-09-20), deep-copied like the first
    }

    [Fact]
    public void Update_RaisesChangedWithTheNewSnapshot()
    {
        using var settings = new AppSettings(_dir);
        AppSettingsData? seen = null;
        settings.Changed += d => seen = d;
        settings.Update(d => d.TtsVoice = "af_bella");
        Assert.NotNull(seen);
        Assert.Equal("af_bella", seen.TtsVoice);
    }

    [Fact]
    public void Update_ThrowingSubscriberDoesNotPropagate()
    {
        using var settings = new AppSettings(_dir);
        settings.Changed += _ => throw new Exception("subscriber");
        var caught = Record.Exception(() => settings.Update(d => d.TtsVoice = "af_bella"));
        Assert.Null(caught);
        Assert.Equal("af_bella", settings.Current.TtsVoice);
    }

    [Fact]
    public void ProfileFile_MissingFields_LoadWithDefaults()
    {
        // A literal, not a round trip of the current type: round-tripping proves nothing about
        // files written by an older build.
        Directory.CreateDirectory(Profiles.Directory(_dir, Profiles.DefaultName));
        File.WriteAllText(Profiles.ProfileFile(_dir, Profiles.DefaultName),
            "{ \"SchemaVersion\": 1, \"LlmUrl\": \"http://old:1234/v1\" }");

        using var settings = new AppSettings(_dir);
        AssertOldShapeLoaded(settings.Current);
    }

    [Fact]
    public void ProfileFile_WithARetiredOrRenamedField_LoadsAndKeepsTheOthers()
    {
        // Show profile name went with the hint row's profile corner (2026-09-15); a profile.json
        // written before then still carries it, and the reader skips what it does not know. The
        // same for a key renamed since (2026-09-17: ThinkingFunVerbs is LlmUseFunVerbs now): no
        // migration, the user's call — the old key is skipped and the default stands. The same
        // again on 2026-09-18: CopyUserText is CopyUserPrompt, and the on/off WebBrowserAllowLan
        // became the WebBrowserNetworkMode picker (a LAN user re-picks once); later that day
        // SkillSlashCommands went with its feature, so the key is a retired one too. On 2026-09-19 the
        // row LLM tools became LLM offer tools and the key followed (LlmOfferTools): a profile that had
        // it off comes back on once, the user's call over a migration.
        // Later still on 2026-09-19 the Files and Web rows took their tab's prefix (File /tree max length,
        // Web SearXNG URL, …) and six keys followed: TreeMaxLength, TreeShowSizes, MentionFolderMode,
        // FileStaleGuard, ViewImageMaxPerCall and SearxngUrl are old spellings now, skipped the same way.
        // Later still that day Reflection verbose went altogether: a retired key, skipped like ShowProfileName.
        // Later still on 2026-09-19 FileStaleLineNumberGuard went with edit_lines (the eight file tools folded into four): retired, skipped the same way.
        // On 2026-09-24 the on/off WelcomeSplash became the WelcomeSplashMode pick: no migration (the user's call), so an old off is fullsize again.
        Directory.CreateDirectory(Profiles.Directory(_dir, Profiles.DefaultName));
        File.WriteAllText(Profiles.ProfileFile(_dir, Profiles.DefaultName),
            "{ \"SchemaVersion\": 1, \"LlmUrl\": \"http://old:1234/v1\", \"ShowProfileName\": false, \"ThinkingFunVerbs\": true, \"LlmUseFunVerbs\": true, \"SpeechOutputEnabled\": true, \"CopyUserText\": false, \"WebBrowserAllowLan\": true, \"SkillSlashCommands\": false, \"LlmTools\": false, \"FileLineNumbers\": true, \"FileStaleLineNumberGuard\": true, \"TreeMaxLength\": 750, \"SearxngUrl\": \"http://old:8080\", \"ReflectionVerbose\": false, \"WelcomeSplash\": false }");

        using var settings = new AppSettings(_dir);
        Assert.Equal("http://old:1234/v1", settings.Current.LlmUrl);
        Assert.True(settings.Current.LlmUseFunVerbs);
        Assert.False(settings.Current.TtsOutput);   // the old key, skipped
        Assert.True(settings.Current.CopyUserPrompt);   // the retired key, skipped
        Assert.Equal("internet", settings.Current.WebBrowserNetworkMode);   // the retired switch, skipped
        Assert.Equal("fullsize", settings.Current.WelcomeSplashMode);   // the retired switch, skipped (2026-09-24)
        Assert.True(settings.Current.SkillHashMention);   // its neighbour untouched by the retired SkillSlashCommands key
        Assert.True(settings.Current.LlmOfferTools);   // the renamed key, skipped; the default stands
        Assert.Equal(["git_delete", "unzip", "zip"], settings.Current.ToolsDisabled);   // no ToolsDisabled key in the old file: the default fills it (a saved [] or ["delete"] would stand)
        Assert.Equal(WorkingDirectory.DefaultTreeLength, settings.Current.FileTreeMaxLength);   // the old TreeMaxLength key, skipped
        Assert.Equal("", settings.Current.WebSearxngUrl);   // the old SearxngUrl key, skipped
        Assert.Equal(1, settings.Current.SchemaVersion);   // read as written; the compiled default is 2
        Assert.Equal(2, new AppSettingsData().SchemaVersion);
    }

    [Fact]
    public void ProfileFile_WithTheInterruptOnAndTheWakeWordOff_LoadsWithTheInterruptOff()
    {
        // The interrupt needs the wake word (2026-09-13); a profile saved before that rule is
        // normalised on load, and the file only follows at the next save.
        Directory.CreateDirectory(Profiles.Directory(_dir, Profiles.DefaultName));
        File.WriteAllText(Profiles.ProfileFile(_dir, Profiles.DefaultName),
            "{ \"SchemaVersion\": 1, \"SttWake\": false, \"SttInterrupt\": true, \"SttInput\": true }");

        using var settings = new AppSettings(_dir);

        Assert.False(settings.Current.SttInterrupt);
        Assert.False(settings.Current.SttWake);
        Assert.True(settings.Current.SttInput);
        Assert.Contains("\"SttInterrupt\": true", File.ReadAllText(Profiles.ProfileFile(_dir, Profiles.DefaultName)));   // untouched until a save
    }

    [Fact]
    public void Pointer_NamingAMissingProfile_WarnsAndLoadsTheDefault_AndIsRewritten()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, AppSettings.FileName), "{ \"SchemaVersion\": 2, \"Profile\": \"ghost\" }");
        var warnings = new List<string>();
        Action<NeonSidekick.Diagnostics.DiagnosticEvent> capture = e => { if (e.Category == "Settings" && e.Level >= NeonSidekick.Diagnostics.DiagnosticLevel.Warning) warnings.Add(e.Message); };
        NeonSidekick.Diagnostics.DiagnosticLog.Emitted += capture;
        try
        {
            using var settings = new AppSettings(_dir);
            Assert.Equal(Profiles.DefaultName, settings.ProfileName);
        }
        finally
        {
            NeonSidekick.Diagnostics.DiagnosticLog.Emitted -= capture;
        }

        Assert.Contains(AppSettings.MissingProfileWarning("ghost"), warnings);
        Assert.Contains("\"Profile\": \"default\"", File.ReadAllText(Path.Combine(_dir, AppSettings.FileName)));
    }

    [Fact]
    public void Pointer_IsHonoured_InTheDirectorysSpelling()
    {
        Profiles.Create(_dir, "Work", new AppSettingsData { LlmModel = "work-model" });
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, AppSettings.FileName), "{ \"SchemaVersion\": 2, \"Profile\": \"work\" }");

        using var settings = new AppSettings(_dir);

        Assert.Equal("Work", settings.ProfileName);
        Assert.Equal("work-model", settings.Current.LlmModel);
        Assert.EndsWith(Path.Combine("profiles", "Work", Profiles.FileName), settings.FilePath);
    }

    [Fact]
    public async Task SwitchProfile_FlushesTheOldOne_LoadsTheOther_RewritesThePointer_AndRaisesChanged()
    {
        Profiles.Create(_dir, "work", new AppSettingsData { LlmModel = "work-model" });
        using var settings = new AppSettings(_dir);
        settings.Update(d => d.LlmModel = "default-model");   // pending in the debounce
        AppSettingsData? seen = null;
        settings.Changed += d => seen = d;

        await settings.SwitchProfileAsync("work");

        Assert.Equal("work", settings.ProfileName);
        Assert.Equal("work-model", settings.Current.LlmModel);
        Assert.Equal("work-model", seen?.LlmModel);
        Assert.Contains("\"Profile\": \"work\"", File.ReadAllText(settings.PointerPath));
        // The debounced edit landed in the old profile's file, not the new one.
        Assert.Contains("default-model", File.ReadAllText(Profiles.ProfileFile(_dir, Profiles.DefaultName)));
        Assert.DoesNotContain("default-model", File.ReadAllText(Profiles.ProfileFile(_dir, "work")));

        // And an edit after the switch goes to the new profile.
        settings.Update(d => d.LlmModel = "work-2");
        await settings.FlushAsync();
        Assert.Contains("work-2", File.ReadAllText(Profiles.ProfileFile(_dir, "work")));
        Assert.DoesNotContain("work-2", File.ReadAllText(Profiles.ProfileFile(_dir, Profiles.DefaultName)));

        using var relaunched = new AppSettings(_dir);
        Assert.Equal("work", relaunched.ProfileName);
        Assert.Equal("work-2", relaunched.Current.LlmModel);
    }

    /// <summary>A temporary profile (<c>_test</c>, 2026-09-24) loads like any other, but the next launch opens default and points the pointer back; the profile is kept.</summary>
    [Fact]
    public async Task Launch_OnATemporaryProfile_LoadsDefault_AndRewritesThePointer()
    {
        Profiles.Create(_dir, "_test", new AppSettingsData { LlmModel = "test-model" });
        using (var settings = new AppSettings(_dir))
        {
            await settings.SwitchProfileAsync("_test");
            Assert.Equal("_test", settings.ProfileName);
            Assert.Equal("test-model", settings.Current.LlmModel);
            Assert.Contains("\"Profile\": \"_test\"", File.ReadAllText(settings.PointerPath));
        }

        var notes = new List<string>();
        Action<NeonSidekick.Diagnostics.DiagnosticEvent> capture = e => { if (e.Category == "Settings") notes.Add(e.Message); };
        NeonSidekick.Diagnostics.DiagnosticLog.Emitted += capture;
        try
        {
            using var relaunched = new AppSettings(_dir);
            Assert.Equal(Profiles.DefaultName, relaunched.ProfileName);
            Assert.Contains("\"Profile\": \"default\"", File.ReadAllText(relaunched.PointerPath));

            // Kept, and a switch still lands in it with its settings.
            Assert.True(Profiles.Exists(_dir, "_test"));
            await relaunched.SwitchProfileAsync("_test");
            Assert.Equal("test-model", relaunched.Current.LlmModel);
        }
        finally
        {
            NeonSidekick.Diagnostics.DiagnosticLog.Emitted -= capture;
        }

        Assert.Contains(AppSettings.TemporaryProfileNotice("_test"), notes);
        Assert.Equal("Profile \"_test\" is temporary (starts with _); loading default.", AppSettings.TemporaryProfileNotice("_test"));
    }

    /// <summary>
    /// <c>--profile</c> / <c>NEONSIDEKICK_PROFILE</c> (2026-09-26): the named profile loads in its listed spelling,
    /// the pointer is left as it is, and the next plain launch follows the pointer again.
    /// </summary>
    [Fact]
    public async Task ProfileOverride_LoadsThatProfile_AndLeavesThePointerAlone()
    {
        Profiles.Create(_dir, "Work", new AppSettingsData { LlmModel = "work-model" });
        Profiles.Create(_dir, "home", new AppSettingsData { LlmModel = "home-model" });
        using (var pointed = new AppSettings(_dir))
        {
            await pointed.SwitchProfileAsync("home");
        }

        string pointer = File.ReadAllText(Path.Combine(_dir, AppSettings.FileName));
        var notes = new List<string>();
        Action<NeonSidekick.Diagnostics.DiagnosticEvent> capture = e => { if (e.Category == "Settings") notes.Add(e.Message); };
        NeonSidekick.Diagnostics.DiagnosticLog.Emitted += capture;
        try
        {
            using var settings = new AppSettings(_dir, "WORK");
            Assert.Equal("Work", settings.ProfileName);
            Assert.Equal("work-model", settings.Current.LlmModel);

            // An edit lands in the overriding profile's file; the pointer is untouched.
            settings.Update(d => d.LlmModel = "work-2");
            await settings.FlushAsync();
            Assert.Contains("work-2", File.ReadAllText(Profiles.ProfileFile(_dir, "Work")));
        }
        finally
        {
            NeonSidekick.Diagnostics.DiagnosticLog.Emitted -= capture;
        }

        Assert.Equal(pointer, File.ReadAllText(Path.Combine(_dir, AppSettings.FileName)));
        Assert.Contains(AppSettings.OverrideProfileNotice("Work"), notes);
        using var relaunched = new AppSettings(_dir);
        Assert.Equal("home", relaunched.ProfileName);
    }

    [Fact]
    public void ProfileOverride_OnAFreshHome_WritesTheDefaultPointer()
    {
        Profiles.Create(_dir, "work", new AppSettingsData());

        using var settings = new AppSettings(_dir, "work");

        Assert.Equal("work", settings.ProfileName);
        Assert.Contains("\"Profile\": \"default\"", File.ReadAllText(settings.PointerPath));
    }

    /// <summary>What a headless run with no profile named hands over (2026-09-26): "default" loads, the pointer stays put.</summary>
    [Fact]
    public async Task ProfileOverride_Default_LeavesThePointerOnAnotherProfile()
    {
        Profiles.Create(_dir, "work", new AppSettingsData());
        using (var tui = new AppSettings(_dir))
        {
            await tui.SwitchProfileAsync("work");
        }

        using var settings = new AppSettings(_dir, Profiles.DefaultName);

        Assert.Equal(Profiles.DefaultName, settings.ProfileName);
        Assert.Contains("\"Profile\": \"work\"", File.ReadAllText(settings.PointerPath));
    }

    [Fact]
    public void ProfileOverride_Default_OnAFreshHome_Loads()
    {
        Assert.True(Profiles.Exists(_dir, Profiles.DefaultName));

        using var settings = new AppSettings(_dir, Profiles.DefaultName);

        Assert.Equal(Profiles.DefaultName, settings.ProfileName);
        Assert.Contains("\"Profile\": \"default\"", File.ReadAllText(settings.PointerPath));
    }

    /// <summary>A temporary profile named on purpose loads; the redirect is for the pointer, not for asking.</summary>
    [Fact]
    public void ProfileOverride_LoadsATemporaryProfile()
    {
        Profiles.Create(_dir, "_test", new AppSettingsData { LlmModel = "test-model" });

        using var settings = new AppSettings(_dir, "_test");

        Assert.Equal("_test", settings.ProfileName);
        Assert.Equal("test-model", settings.Current.LlmModel);
    }

    [Theory]
    [InlineData("ghost")]
    [InlineData("..")]
    public void ProfileOverride_Unknown_Throws_NamingTheProfilesThere(string name)
    {
        Profiles.Create(_dir, "work", new AppSettingsData());

        var ex = Assert.Throws<ArgumentException>(() => new AppSettings(_dir, name));

        Assert.StartsWith(AppSettings.UnknownProfileMessage(name, Profiles.List(_dir)), ex.Message);
        Assert.Contains("default, work", ex.Message);
        Assert.False(File.Exists(Path.Combine(_dir, AppSettings.FileName)));   // nothing written for a bad name
    }

    [Fact]
    public async Task ProfileOverride_ThenASwitch_RewritesThePointerAsAlways()
    {
        Profiles.Create(_dir, "work", new AppSettingsData());
        Profiles.Create(_dir, "home", new AppSettingsData());
        using var settings = new AppSettings(_dir, "work");

        await settings.SwitchProfileAsync("home");

        Assert.Contains("\"Profile\": \"home\"", File.ReadAllText(settings.PointerPath));
    }

    [Fact]
    public async Task SwitchProfile_ToADirectoryWithoutAFile_LoadsDefaults()
    {
        Directory.CreateDirectory(Profiles.Directory(_dir, "empty"));
        using var settings = new AppSettings(_dir);
        settings.Update(d => d.LlmModel = "default-model");

        await settings.SwitchProfileAsync("empty");

        AssertSame(new AppSettingsData(), settings.Current);
    }

    [Fact]
    public void Constructor_CreatesTheGlobalAndTheProfilesSkillsFolders()
    {
        // 2026-09-18, the user's call: both roots exist from the first start, so a skill can be
        // dropped in without making the folder first.
        using var settings = new AppSettings(_dir);

        Assert.True(Directory.Exists(settings.GlobalSkillsDirectory));
        Assert.True(Directory.Exists(settings.ProfileSkillsDirectory));
        Assert.Equal(Path.Combine(_dir, "skills"), settings.GlobalSkillsDirectory);
        Assert.Equal(Path.Combine(Profiles.Directory(_dir, Profiles.DefaultName), "skills"), settings.ProfileSkillsDirectory);
    }

    [Fact]
    public async Task SwitchProfile_CreatesTheNewProfilesSkillsFolder_NeverAnothers()
    {
        Directory.CreateDirectory(Profiles.Directory(_dir, "work"));
        Directory.CreateDirectory(Profiles.Directory(_dir, "other"));
        using var settings = new AppSettings(_dir);
        Assert.False(Directory.Exists(Path.Combine(Profiles.Directory(_dir, "work"), "skills")));

        await settings.SwitchProfileAsync("work");

        Assert.True(Directory.Exists(settings.ProfileSkillsDirectory));
        Assert.EndsWith(Path.Combine("work", "skills"), settings.ProfileSkillsDirectory);
        Assert.False(Directory.Exists(Path.Combine(Profiles.Directory(_dir, "other"), "skills")));   // nothing is created for a look
    }

    [Fact]
    public async Task ResetProfile_TheLoadedOne_KeepsItsSkillsFolder()
    {
        using var settings = new AppSettings(_dir);
        Directory.Delete(settings.ProfileSkillsDirectory);

        await settings.ResetProfileAsync(Profiles.DefaultName);

        Assert.True(Directory.Exists(settings.ProfileSkillsDirectory));
    }

    [Fact]
    public void SkillsFolderWarning_IsPinned()
    {
        Assert.Equal(@"Could not create the skills folder C:\x\skills: denied", AppSettings.SkillsFolderWarning(@"C:\x\skills", "denied"));
    }

    [Fact]
    public void Constructor_CreatesTheProfilesSplashFolder()
    {
        // 2026-09-22, the user's ask: nothing created it before, so using a profile's own splash
        // pictures meant making the folder by hand. Empty, it is no splash at all — SplashImages
        // reads a folder with no picture as none and the embedded set stands.
        using var settings = new AppSettings(_dir);

        Assert.True(Directory.Exists(settings.ProfileSplashDirectory));
        Assert.Equal(Path.Combine(Profiles.Directory(_dir, Profiles.DefaultName), "splash"), settings.ProfileSplashDirectory);
        Assert.Equal(SplashImages.ProfileFolderName, Path.GetFileName(settings.ProfileSplashDirectory));
        Assert.Null(SplashImages.FromDirectory(settings.ProfileSplashDirectory));   // made, but empty: the embedded set is still in force
    }

    [Fact]
    public async Task SwitchProfile_CreatesTheNewProfilesSplashFolder_NeverAnothers()
    {
        Directory.CreateDirectory(Profiles.Directory(_dir, "work"));
        Directory.CreateDirectory(Profiles.Directory(_dir, "other"));
        using var settings = new AppSettings(_dir);
        Assert.False(Directory.Exists(Path.Combine(Profiles.Directory(_dir, "work"), "splash")));

        await settings.SwitchProfileAsync("work");

        Assert.True(Directory.Exists(settings.ProfileSplashDirectory));
        Assert.EndsWith(Path.Combine("work", "splash"), settings.ProfileSplashDirectory);
        Assert.False(Directory.Exists(Path.Combine(Profiles.Directory(_dir, "other"), "splash")));   // nothing is created for a look
    }

    [Fact]
    public async Task ResetProfile_TheLoadedOne_KeepsItsSplashFolder()
    {
        using var settings = new AppSettings(_dir);
        Directory.Delete(settings.ProfileSplashDirectory);

        await settings.ResetProfileAsync(Profiles.DefaultName);

        Assert.True(Directory.Exists(settings.ProfileSplashDirectory));
    }

    [Fact]
    public void Reload_RemakesTheProfileFolders()
    {
        using var settings = new AppSettings(_dir);
        Directory.Delete(settings.ProfileSplashDirectory);
        Directory.Delete(settings.ProfileSkillsDirectory);

        settings.Reload();

        Assert.True(Directory.Exists(settings.ProfileSplashDirectory));
        Assert.True(Directory.Exists(settings.ProfileSkillsDirectory));
    }

    [Fact]
    public void SplashFolderWarning_IsPinned()
    {
        Assert.Equal(@"Could not create the splash folder C:\x\splash: denied", AppSettings.SplashFolderWarning(@"C:\x\splash", "denied"));
    }

    [Fact]
    public async Task SwitchProfile_UnknownName_Throws()
    {
        using var settings = new AppSettings(_dir);
        await Assert.ThrowsAsync<ArgumentException>(() => settings.SwitchProfileAsync("ghost"));
        Assert.Equal(Profiles.DefaultName, settings.ProfileName);
    }

    [Fact]
    public async Task ResetProfile_TheLoadedOne_All_FlushesThenLoadsTheDefaults_AndRaisesChanged()
    {
        Profiles.Create(_dir, "work", new AppSettingsData { LlmModel = "work-model" });
        using var settings = new AppSettings(_dir);
        await settings.SwitchProfileAsync("work");
        File.WriteAllText(Path.Combine(settings.ProfileDirectory, "persona.md"), "You are Rex.");
        settings.Update(d => d.LlmModel = "work-2");   // pending in the debounce: must not land after the reset
        var seen = new List<AppSettingsData>();
        settings.Changed += seen.Add;

        await settings.ResetProfileAsync("WORK", all: true);

        Assert.Equal("work", settings.ProfileName);
        AssertSame(new AppSettingsData(), settings.Current);
        Assert.Single(seen);
        AssertSame(new AppSettingsData(), seen[0]);
        Assert.Equal("You are Rex.", File.ReadAllText(Path.Combine(settings.ProfileDirectory, "persona.md")));   // the sidekick's files stay through a reset (2026-09-20)
        Assert.Contains("\"Profile\": \"work\"", File.ReadAllText(settings.PointerPath));

        // The debounce never fires again: the file stays at the defaults.
        await Task.Delay(TimeSpan.FromMilliseconds(800));
        Assert.DoesNotContain("work-2", File.ReadAllText(Profiles.ProfileFile(_dir, "work")));
        Assert.DoesNotContain("work-model", File.ReadAllText(Profiles.ProfileFile(_dir, "work")));

        // And an edit after the reset lands as usual.
        settings.Update(d => d.LlmModel = "work-3");
        await settings.FlushAsync();
        Assert.Contains("work-3", File.ReadAllText(Profiles.ProfileFile(_dir, "work")));
    }

    [Fact]
    public async Task Reload_ReadsTheFileAgain_DropsThePendingSave_AndRaisesChanged()
    {
        // /profile reload (2026-09-21): the hand-edited file wins over the debounced save, and a missing file is the defaults.
        Profiles.Create(_dir, "work", new AppSettingsData { LlmModel = "work-model" });
        using var settings = new AppSettings(_dir);
        await settings.SwitchProfileAsync("work");
        settings.Update(d => d.LlmModel = "work-2");   // pending in the debounce: must not land after the reload
        string file = Profiles.ProfileFile(_dir, "work");
        File.WriteAllText(file, JsonSerializer.Serialize(new AppSettingsData { LlmModel = "by-hand", TtsSpeed = 1.4 }, SettingsJsonContext.Default.AppSettingsData));
        var seen = new List<AppSettingsData>();
        settings.Changed += seen.Add;

        settings.Reload();

        Assert.Equal("by-hand", settings.Current.LlmModel);
        Assert.Equal(1.4, settings.Current.TtsSpeed);
        Assert.Equal("work", settings.ProfileName);
        Assert.Single(seen);
        Assert.Equal("by-hand", seen[0].LlmModel);
        await Task.Delay(TimeSpan.FromMilliseconds(800));
        Assert.Contains("by-hand", File.ReadAllText(file));   // the debounce never fired
        Assert.DoesNotContain("work-2", File.ReadAllText(file));

        // An edit after the reload lands as usual, over the reloaded values.
        settings.Update(d => d.LlmModel = "work-3");
        await settings.FlushAsync();
        Assert.Contains("work-3", File.ReadAllText(file));
        Assert.Contains("1.4", File.ReadAllText(file));

        // The file gone: the defaults, as a launch would find.
        File.Delete(file);
        settings.Reload();
        AssertSame(new AppSettingsData(), settings.Current);
    }

    [Fact]
    public async Task ResetProfile_TheLoadedOne_KeepsItsCurrentUrlsPathsAndKeys()
    {
        // 2026-09-27: a plain reset keeps Profiles.ResetKeptSettings — the pending save's values, since the flush comes first.
        Profiles.Create(_dir, "work", new AppSettingsData { LlmModel = "work-model", TtsSpeed = 1.4 });
        using var settings = new AppSettings(_dir);
        await settings.SwitchProfileAsync("work");
        settings.Update(d => { d.LlmModel = "work-2"; d.LlmUrl = "http://llm:1234/v1"; d.ObsidianVault = @"D:\Vault"; });
        var seen = new List<AppSettingsData>();
        settings.Changed += seen.Add;

        await settings.ResetProfileAsync("work");

        Assert.Equal("work-2", settings.Current.LlmModel);
        Assert.Equal("http://llm:1234/v1", settings.Current.LlmUrl);
        Assert.Equal(@"D:\Vault", settings.Current.ObsidianVault);
        Assert.Equal(new AppSettingsData().TtsSpeed, settings.Current.TtsSpeed);
        Assert.Equal("work-2", Assert.Single(seen).LlmModel);
        Assert.Equal("work-2", Profiles.ReadProfileFile(Profiles.ProfileFile(_dir, "work")).LlmModel);
    }

    [Fact]
    public async Task ResetProfile_AnotherOne_LeavesTheLoadedDataAlone()
    {
        Profiles.Create(_dir, "work", new AppSettingsData { LlmModel = "work-model", TtsSpeed = 1.4 });
        File.WriteAllText(Path.Combine(Profiles.Directory(_dir, "work"), "memory.json"), "[]");
        using var settings = new AppSettings(_dir);
        settings.Update(d => d.LlmModel = "default-model");
        int changes = 0;
        settings.Changed += _ => changes++;

        await settings.ResetProfileAsync("work");

        Assert.True(File.Exists(Path.Combine(Profiles.Directory(_dir, "work"), "memory.json")));   // the memories stay (2026-09-20)
        Assert.Equal(0, changes);
        Assert.Equal(Profiles.DefaultName, settings.ProfileName);
        Assert.Equal("default-model", settings.Current.LlmModel);
        var work = Profiles.ReadProfileFile(Profiles.ProfileFile(_dir, "work"));
        Assert.Equal("work-model", work.LlmModel);   // kept (2026-09-27)
        Assert.Equal(new AppSettingsData().TtsSpeed, work.TtsSpeed);
        await settings.FlushAsync();
        Assert.Contains("default-model", File.ReadAllText(Profiles.ProfileFile(_dir, Profiles.DefaultName)));
    }

    [Fact]
    public async Task ResetProfile_UnknownName_Throws()
    {
        using var settings = new AppSettings(_dir);
        await Assert.ThrowsAsync<ArgumentException>(() => settings.ResetProfileAsync("ghost"));
        Assert.Equal(Profiles.DefaultName, settings.ProfileName);
    }

    private static void AssertOldShapeLoaded(AppSettingsData s)
    {
        Assert.Equal("http://old:1234/v1", s.LlmUrl);
        Assert.Equal("af_heart", s.TtsVoice);
        Assert.Equal("http://localhost:8880/v1", s.TtsHttpUrl);
        Assert.False(s.TtsOutput);
        Assert.False(s.SttInput);
        Assert.False(s.SttWake);
        Assert.Equal("hey neon", s.SttWakePhrase);
        Assert.Equal("F4", s.SttPushToTalkKey);
        Assert.Equal(1.2, s.TtsSpeed);   // 1.0 until 2026-09-18
        Assert.Equal("ggml-base.en.bin", s.SttWhisperModel);
        Assert.False(s.SttInterrupt);
        Assert.Equal("none", s.LlmReasoning);
        Assert.Equal("am_eric", s.TtsVoice2);   // the defaults since 2026-09-16 fill the missing fields
        Assert.Equal(80, s.TtsVoiceMix);
        Assert.True(s.Memory);
        Assert.Equal(100, s.SttInterruptEchoGuard);
        Assert.Equal(200, s.SttInterruptConfirmMs);   // 150 until 2026-09-17
        Assert.Equal("", s.WorkingDirectory);
        Assert.True(s.CommandTypoIntercept);
        Assert.True(s.KeepCommandHistory);   // 2026-09-25
        Assert.True(s.CopyUserPrompt);
        Assert.True(s.HideExitAutocomplete);
        Assert.Equal(0, s.LlmContextLength);
        Assert.True(s.ShowImageThumbnails);
        Assert.Equal("summary", s.LlmCompactType);
        Assert.Equal(2, s.LlmCompactKeepRecent);
        Assert.False(s.LlmCompactShowSummary);   // 2026-09-21: the one notice line unless asked
        Assert.Equal(85, s.LlmAutoCompactPercent);
        Assert.Equal(0, s.LlmMaxTurns);
        Assert.Equal(10000, s.LlmMaxToolIterations);
        Assert.Equal("small", s.ImageThumbnailSize);
        Assert.Equal("synthwave", s.Theme);
        Assert.Equal(500, s.FileTreeMaxLength);
        Assert.True(s.FileTreeShowSizes);
        Assert.Equal(10, s.FileViewImageMaxPerCall);   // 2026-09-19 (a constant 4 in the tool until then)
        Assert.Equal("basic", s.NewProfileMode);
        Assert.True(s.LlmOfferTools);
        Assert.False(s.LlmUseFunVerbs);
        Assert.True(s.LlmShowThinking);
        Assert.Equal("local", s.LlmScanMode);
        Assert.True(s.WebTools);
        Assert.Equal("default", s.WebBrowserMode);
        Assert.Equal("", s.WebBrowserPath);
        Assert.Equal("internet", s.WebBrowserNetworkMode);
        Assert.Equal("", s.WebSearxngUrl);
        Assert.Equal(20, s.WebSearchMaxResults);
        Assert.Equal(1, AppSettingsData.MinWebSearchMaxResults);
        Assert.Equal(20, AppSettingsData.MaxWebSearchMaxResults);
        Assert.Equal(20, AppSettingsData.DefaultWebSearchMaxResults);
        Assert.True(s.TtsVoicePreview);
        Assert.Equal("prune", s.LlmToolCompactType);
        Assert.True(s.FileTools);
        Assert.Equal("duckduckgo", s.WebSearchMethod);
        // The Ask tab (2026-09-15): the tool on, ten questions of ten choices; a choice needs two, so the choices floor is 2.
        Assert.True(s.AskUser);
        Assert.Equal(10, s.AskMaxQuestions);
        Assert.Equal(10, s.AskMaxChoices);
        Assert.Equal("in-process", s.TtsSource);
        Assert.Equal("vosk-model-small-en-us-0.15", s.SttVoskModel);
        Assert.Equal("folder-remain", s.FileMentionFolderMode);
        Assert.True(s.AgentSkills);
        Assert.False(s.ExternalSkills);
        Assert.True(s.ProjectFile);   // later on 2026-09-19: the notes read unless the Project tab's toggle says not
        Assert.Equal("protected", s.SkillCompactMode);
        Assert.True(s.TranscriptMarkdown);
        Assert.Equal("fullsize", s.WelcomeSplashMode);   // 2026-09-18; a pick since 2026-09-24 (on was fullsize)
        Assert.False(s.ShowWorkingDirectory);   // 2026-09-18; off by default since 2026-09-21
        Assert.True(s.ShowToolbar);   // 2026-09-21
        Assert.True(s.ThemedViewer);   // later on 2026-09-27
        Assert.Equal("", s.DraftEditor);   // 2026-09-19: the shell's default for .txt
        // The Sessions tab (2026-09-18): logging and the tool on, the model writes the title (the first line until later that day), kept forever, ten hits.
        Assert.True(s.SessionLogging);
        Assert.Equal("model-written", s.SessionNamingMode);
        Assert.Equal(0, s.SessionRetentionDays);
        Assert.Equal(10, s.SessionSearchMaxResults);
        Assert.Equal("all-names", s.SessionShowName);   // the rule above the input row names the session (later on 2026-09-18)
        Assert.True(s.SessionTool);
        // The message queue (2026-09-18): on, a cancelled reply holds it.
        Assert.True(s.QueueMessages);
        Assert.Equal("empty", s.QueueCancelMode);   // hold until 2026-09-20
        // The paste preview (2026-09-16): 25 lines of a collapsed paste under the sent line, 0 = the label alone.
        Assert.Equal(25, s.PastePreviewLines);
        Assert.Equal(25, PasteBlocks.DefaultPreviewLines);
        Assert.Equal(200, PasteBlocks.MaxPreviewLines);
        // The Files-tab Safe edits switch (2026-09-17; the stale-number guard beside it until later on 2026-09-19): off out of the box since 2026-09-19, the user's call.
        Assert.False(s.FileSafeEdits);
        Assert.True(s.SkillHashMention);
        Assert.True(s.ToolsDollarMention);   // 2026-09-19
        Assert.Equal([NeonSidekick.Llm.Tools.GitDeleteTool.ToolName, NeonSidekick.Llm.Tools.UnzipTool.ToolName, NeonSidekick.Llm.Tools.ZipTool.ToolName], s.ToolsDisabled);   // git_delete since 2026-09-20 (the user's call; git_discard with it until 2026-09-23, the user's call again), zip and unzip since 2026-09-21; delete was opt-in from 2026-09-20 until later on 2026-09-21 (the user's call both times); a saved list stands
        // The git tools (2026-09-20): on, 500 patch lines (20–5000), 20 commits (1–200).
        Assert.False(s.GitNativeTools);   // off by default since later on 2026-09-21 (on from 2026-09-20): the model reaches git through the shell unless the profile opts in
        Assert.Equal(500, s.GitNativeDiffMaxLines);
        Assert.Equal(20, s.GitNativeLogMaxCommits);
        Assert.Equal("", s.GitNativeEmail);   // the /gituser pair (2026-09-21): not set until typed
        Assert.Equal("", s.GitNativeName);
        // The shell tools (2026-09-21): ask before anything runs, nothing allowed for good, PowerShell, 180 s (1–3600) under a 600 s cap (10–3600), 30,000 chars of output (2000–500000).
        Assert.Equal("ask", s.ShellCommandPolicy);
        Assert.Equal("ask", NeonSidekick.Shell.CommandPolicy.Default);
        Assert.Empty(s.ShellCommandAllowed);
        Assert.Equal("powershell", s.ShellDefault);
        Assert.Equal("powershell", NeonSidekick.Shell.ShellKinds.Default);
        Assert.Equal(180, s.ShellTimeoutSeconds);
        Assert.Equal(1, AppSettingsData.MinShellTimeoutSeconds);
        Assert.Equal(3600, AppSettingsData.MaxShellTimeoutSeconds);
        Assert.Equal(600, s.ShellForegroundCapSeconds);
        Assert.Equal(10, AppSettingsData.MinShellForegroundCapSeconds);
        Assert.Equal(3600, AppSettingsData.MaxShellForegroundCapSeconds);
        Assert.Equal(30000, s.ShellOutputMaxChars);
        Assert.Equal(2000, AppSettingsData.MinShellOutputMaxChars);
        Assert.Equal(500000, AppSettingsData.MaxShellOutputMaxChars);
        // execute_code (2026-09-21): every language the machine has, 300 s (1–3600), 50 tool calls a run (1–500).
        Assert.Equal(["powershell", "python", "node"], s.ShellCodeLanguages);
        Assert.Equal(["powershell", "python", "node"], NeonSidekick.Shell.CodeLanguages.Default);
        Assert.Equal(300, s.ShellCodeTimeoutSeconds);
        Assert.Equal(1, AppSettingsData.MinShellCodeTimeoutSeconds);
        Assert.Equal(3600, AppSettingsData.MaxShellCodeTimeoutSeconds);
        Assert.False(s.ShellToolBridge);   // later on 2026-09-21: a script does everything itself unless asked
        Assert.True(s.ShellPoliceOutsidePaths);   // 2026-09-22: a command, a script or text to a process stays under the working directory unless the user turns it off
        Assert.True(s.ShellPreferNative);   // 2026-09-26: a line a native tool covers goes back to that tool unless the user turns it off
        Assert.Equal(50, s.ShellCodeMaxToolCalls);
        Assert.Equal(1, AppSettingsData.MinShellCodeMaxToolCalls);
        Assert.Equal(500, AppSettingsData.MaxShellCodeMaxToolCalls);
        // The MCP servers (2026-09-20): off by default since 2026-09-21, none switched off, 30 s to connect (5–300).
        Assert.False(s.McpServers);
        Assert.Empty(s.McpServersDisabled);
        Assert.Equal(30, s.McpConnectTimeoutSeconds);
        Assert.Equal(5, AppSettingsData.MinMcpConnectTimeout);
        Assert.Equal(300, AppSettingsData.MaxMcpConnectTimeout);
        // Reflection (auto-learn) (2026-09-17): on out of the box, the reflection at the profile's own level.
        Assert.True(s.ReflectionAutoLearn);
        Assert.Equal("none", s.ReflectionReasoning);   // the profile's level (profile-default) until later on 2026-09-19
        Assert.Equal(NeonSidekick.Skills.ReflectionReasoning.Default, s.ReflectionReasoning);
        Assert.Equal(3, s.ReflectionWindow);
        Assert.Equal(1, AppSettingsData.MinReflectionWindow);
        Assert.Equal(5, AppSettingsData.MaxReflectionWindow);
        Assert.Equal(4, s.ReflectionMinToolCalls);   // 5 until 2026-09-17
        Assert.Equal(3, AppSettingsData.MinReflectionMinToolCalls);
        Assert.Equal(20, AppSettingsData.MaxReflectionMinToolCalls);
        // Reflection max requests (2026-09-17): the constant of that morning, as the default.
        Assert.Equal(4, s.ReflectionMaxRequests);
        Assert.Equal(1, AppSettingsData.MinReflectionMaxRequests);
        Assert.Equal(20, AppSettingsData.MaxReflectionMaxRequests);
        // The cooldown and the sessions evidence (2026-09-19): five minutes (30 for an hour), 0 = off, scoped to the skill just written; the earlier sessions open every reflection.
        Assert.Equal(5, s.ReflectionCooldownMinutes);
        Assert.Equal("last-written-skill", s.ReflectionCooldownMode);
        Assert.Equal(0, AppSettingsData.MinReflectionCooldownMinutes);
        Assert.Equal(1440, AppSettingsData.MaxReflectionCooldownMinutes);
        Assert.True(s.ReflectionIncludesSessions);
        Assert.True(s.ReflectionYieldsToTurns);
        Assert.False(s.ReflectionEditsSupportingFiles);   // 2026-09-27: off for now, the user's call
        Assert.Equal(1, AppSettingsData.MinAskMaxQuestions);
        Assert.Equal(10, AppSettingsData.MaxAskMaxQuestions);
        Assert.Equal(10, AppSettingsData.DefaultAskMaxQuestions);
        Assert.Equal(2, AppSettingsData.MinAskMaxChoices);
        Assert.Equal(15, AppSettingsData.MaxAskMaxChoices);
        Assert.Equal(10, AppSettingsData.DefaultAskMaxChoices);
    }

    [Fact]
    public void CorruptProfileFile_FallsBackToDefaults()
    {
        Directory.CreateDirectory(Profiles.Directory(_dir, Profiles.DefaultName));
        File.WriteAllText(Profiles.ProfileFile(_dir, Profiles.DefaultName), "{ this is not json");

        using var settings = new AppSettings(_dir);
        AssertSame(new AppSettingsData(), settings.Current);
    }

    [Fact]
    public void CorruptPointerFile_LoadsTheDefaultProfile()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, AppSettings.FileName), "{ this is not json");
        Profiles.Create(_dir, Profiles.DefaultName, new AppSettingsData { LlmModel = "kept" });

        using var settings = new AppSettings(_dir);
        Assert.Equal(Profiles.DefaultName, settings.ProfileName);
        Assert.Equal("kept", settings.Current.LlmModel);
    }

    [Fact]
    public void ResolveStorageDirectory_PrefersTheOverride()
    {
        string resolved = AppSettings.ResolveStorageDirectory(_dir);
        Assert.Equal(Path.GetFullPath(_dir), resolved);
    }

    [Fact]
    public void ResolveStorageDirectory_DefaultsUnderTheUserProfile()
    {
        string resolved = AppSettings.ResolveStorageDirectory(null);
        Assert.EndsWith(AppSettings.DefaultDirectoryName, resolved);
    }
    [Fact]
    public void Update_LogsOneChangedLinePerProperty_AndNothingForANoOp()
    {
        using var settings = new AppSettings(_dir);
        var lines = new List<NeonSidekick.Diagnostics.DiagnosticEvent>();
        Action<NeonSidekick.Diagnostics.DiagnosticEvent> capture = e => { if (e.Category == AppSettings.Category && e.Message.StartsWith("Changed ", StringComparison.Ordinal)) lines.Add(e); };
        NeonSidekick.Diagnostics.DiagnosticLog.Emitted += capture;
        try
        {
            settings.Update(d => { d.TtsSpeed = 1.3; d.TtsOutput = true; d.LlmApiKey = "sk-secret"; });
            settings.Update(d => d.TtsSpeed = 1.3);   // picked again: unchanged
        }
        finally
        {
            NeonSidekick.Diagnostics.DiagnosticLog.Emitted -= capture;
        }

        Assert.Equal(["Changed LlmApiKey: (redacted)", "Changed TtsOutput: false → true", "Changed TtsSpeed: 1.2 → 1.3"], lines.Select(e => e.Message));
        Assert.All(lines, e => Assert.Equal(NeonSidekick.Diagnostics.DiagnosticLevel.Info, e.Level));
        Assert.Equal("Changed TtsSpeed: 1 → 1.2", AppSettings.ChangedLogLine("TtsSpeed: 1 → 1.2"));
        Assert.Equal("Settings", AppSettings.Category);
    }
}
