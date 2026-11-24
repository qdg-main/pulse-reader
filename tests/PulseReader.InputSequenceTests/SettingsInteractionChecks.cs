using PulseReader.Settings;
using PulseReader.Configuration;
using PulseReader.Hotkeys;
using PulseReader.Speech;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

internal static class SettingsInteractionChecks
{
    public static void ShowPreview()
    {
        Application.EnableVisualStyles();
        using var form = CreatePreviewForm();
        form.ShowInTaskbar = true;
        form.ShowNearTray();
        Application.Run();
    }

    public static void SaveReadmeScreenshot(string outputPath)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Application.EnableVisualStyles();
                using var form = CreatePreviewForm();
                form.ApplyWorkingAreaForTest(new Rectangle(0, 0, 1920, 1080));
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-form.Width - 32, 32);
                form.Show();
                Application.DoEvents();
                form.PerformLayout();

                using var content = new Bitmap(
                    form.ClientSize.Width,
                    form.ClientSize.Height,
                    PixelFormat.Format32bppPArgb);
                form.DrawToBitmap(content, new Rectangle(Point.Empty, form.ClientSize));

                using var composed = SettingsWindowChrome.RenderChrome(form.ClientSize);
                using (var graphics = Graphics.FromImage(composed))
                using (var contentPath = PulseReaderTheme.RoundedRectangle(
                           new RectangleF(
                               SettingsWindowChrome.ContentInset,
                               SettingsWindowChrome.ContentInset,
                               form.ClientSize.Width - SettingsWindowChrome.ContentInset * 2,
                               form.ClientSize.Height - SettingsWindowChrome.ContentInset * 2),
                           SettingsWindowChrome.CornerRadius - SettingsWindowChrome.ContentInset))
                {
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    graphics.SetClip(contentPath);
                    graphics.DrawImageUnscaled(content, Point.Empty);
                }

                var fullPath = Path.GetFullPath(outputPath);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                composed.Save(fullPath, ImageFormat.Png);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw new InvalidOperationException(
                "The Settings README screenshot could not be rendered.",
                failure);
        }
    }

    public static void Run()
    {
        using var hotkeyCapture = new HotkeyCaptureBox();
        using var voicePicker = new ThemedComboBox();

        Assert(
            (object)hotkeyCapture is not System.Windows.Forms.TextBox,
            "The hotkey capture surface still exposes editable TextBox behavior.");
        Assert(
            (object)voicePicker is not System.Windows.Forms.ComboBox,
            "The themed picker still relies on the native ComboBox arrow that flickers white.");
        Assert(
            ThemedComboBox.IsListItemHighlighted(DrawItemState.None, 4, 4)
            && !ThemedComboBox.IsListItemHighlighted(DrawItemState.None, 3, 4),
            "The themed picker does not distinguish the row under the pointer.");
        Assert(
            hotkeyCapture.TryCapture(Keys.Control | Keys.Alt | Keys.F8)
            && hotkeyCapture.Hotkey?.DisplayName == "Ctrl + Alt + F8",
            "The non-editable hotkey control did not capture a modified function key.");
        Assert(
            !hotkeyCapture.TryCapture(Keys.F8),
            "The hotkey control accepted an unmodified key.");

        var heart = KokoroVoiceCatalog.Get(KokoroSpeechSettings.DefaultVoice);
        Assert(
            SettingsPresentation.PreviewText(SpeechBackend.Windows, heart)
                == SettingsPresentation.WindowsPreviewText,
            "Windows preview text still names the selected Kokoro voice.");
        Assert(
            SettingsPresentation.PlayingStatus(SpeechBackend.Windows, heart)
                == "Playing the Windows speech voice.",
            "Windows preview status still names the selected Kokoro voice.");
        var windowsVoice = new WindowsVoiceDescriptor(
            "Microsoft Hazel Desktop",
            "Microsoft Hazel Desktop",
            "English (United Kingdom)",
            "en-GB",
            true);
        Assert(
            SettingsPresentation.PreviewText(windowsVoice)
                == "Hello, this is Microsoft Hazel Desktop. Pulse Reader is using the Windows speech engine."
            && SettingsPresentation.PlayingStatus(windowsVoice)
                == "Playing Microsoft Hazel Desktop.",
            "Windows preview copy does not identify the selected Windows voice.");
        var sky = KokoroVoiceCatalog.Get(KokoroSpeechSettings.DefaultSecondaryVoice);
        Assert(
            SettingsPresentation.PreviewText(SpeechBackend.Kokoro, heart, true, sky, 65)
                == "Hello, this is Heart and Sky blended together in Pulse Reader, with 65 percent Heart and 35 percent Sky.",
            "The blended preview text does not identify both voices and their weights.");
        Assert(
            SettingsPresentation.PlayingStatus(SpeechBackend.Kokoro, heart, true, sky, 65)
                == "Playing Heart 65% + Sky 35%.",
            "The blended preview status does not name both voices and their weights.");
        Assert(
            SettingsPresentation.PreviewButtonText(SpeechBackend.Kokoro, true)
                == "■  Stop voice",
            "The active preview button does not become a stop action.");
        Assert(
            SettingsPresentation.PreviewButtonText(SpeechBackend.Windows, false)
                == "▶  Preview voice",
            "The idle Windows preview button still uses backend-specific copy.");

        CheckPickerSelectionClosesCleanly();
        CheckWindowCornerUsesPartialAlpha();
        CheckSettingsFormStateAndLayout();
        CheckUnavailableContextualModelCanBeDisabled();
        CheckWindowsVoiceControls();
        CheckStoppedPreviewIgnoresLateStart();

        Console.WriteLine("PASS: settings input controls avoid native editing and paint flicker paths.");
    }

    private static SettingsForm CreatePreviewForm() =>
        new(
            AppConfiguration.Default,
            HotkeyDefinition.Default,
            _ => null,
            _ => null,
            _ => Task.FromResult(SpeechStartResult.CanceledResult),
            () => { },
            () => null,
            () => null,
            contextualPronunciationAvailability:
                new ContextualPronunciationAvailability(true, "test", null));

    private static void CheckWindowsVoiceControls()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                WindowsVoiceDescriptor[] windowsVoices =
                [
                    new(
                        "Microsoft George Desktop",
                        "Microsoft George Desktop",
                        "English (United Kingdom)",
                        "en-GB",
                        true),
                    new(
                        "Microsoft David Desktop",
                        "Microsoft David Desktop",
                        "English (United States)",
                        "en-US",
                        false),
                    new(
                        "Microsoft Zira Desktop",
                        "Microsoft Zira Desktop",
                        "English (United States)",
                        "en-US",
                        false)
                ];
                AppConfiguration? appliedConfiguration = null;
                using var form = new SettingsForm(
                    AppConfiguration.Default,
                    HotkeyDefinition.Default,
                    configuration =>
                    {
                        appliedConfiguration = configuration;
                        return null;
                    },
                    _ => null,
                    _ => Task.FromResult(SpeechStartResult.Success()),
                    () => { },
                    () => null,
                    () => null,
                    windowsVoices);
                form.Show();
                Application.DoEvents();

                var viewport = form.Controls.OfType<SettingsScrollViewport>().Single();
                var voicePanel = viewport.ContentControls
                    .OfType<ThemePanel>()
                    .OrderBy(panel => panel.Top)
                    .First();
                var pickers = voicePanel.Controls.OfType<ThemedComboBox>().ToArray();
                var backendBox = pickers.Single(box => box.Top == 34);
                var languageBox = pickers.Single(box => box.Top == 98);
                var voiceBox = pickers.Single(box => box.Top == 162);
                var speedSlider = voicePanel.Controls
                    .OfType<GlowSlider>()
                    .Single(slider => slider.Top == 258);
                var volumeSlider = voicePanel.Controls
                    .OfType<GlowSlider>()
                    .Single(slider => slider.Top == 313);
                var blendHeader = voicePanel.Controls.OfType<BlendSectionHeader>().Single();

                backendBox.OpenDropDown();
                backendBox.CommitSelection(1);
                Application.DoEvents();

                Assert(
                    Equals(backendBox.SelectedItem, SpeechBackend.Windows)
                    && languageBox.Enabled
                    && voiceBox.Enabled
                    && !blendHeader.Enabled,
                    "Switching to Windows did not enable its voice controls and disable Kokoro blending.");
                Assert(
                    languageBox.Items.Cast<string>().SequenceEqual(
                    [
                        "English (United Kingdom)",
                        "English (United States)"
                    ])
                    && Equals(languageBox.SelectedItem, "English (United Kingdom)"),
                    "Windows voices were not grouped by their installed language.");
                Assert(
                    voiceBox.Items.Cast<WindowsVoiceDescriptor>().Single().Id
                        == "Microsoft George Desktop"
                    && speedSlider.Minimum == WindowsSpeechSettings.MinimumRate
                    && speedSlider.Maximum == WindowsSpeechSettings.MaximumRate
                    && speedSlider.Value == WindowsSpeechSettings.DefaultRate
                    && volumeSlider.Value == WindowsSpeechSettings.DefaultVolume,
                    "Windows did not load its default voice, rate, and volume controls.");

                languageBox.OpenDropDown();
                languageBox.CommitSelection(1);
                Application.DoEvents();
                voiceBox.OpenDropDown();
                voiceBox.CommitSelection(1);
                speedSlider.Value = 4;
                volumeSlider.Value = 61;
                Application.DoEvents();

                Assert(
                    appliedConfiguration is
                    {
                        SpeechBackend: SpeechBackend.Windows,
                        WindowsVoice: "Microsoft Zira Desktop",
                        WindowsSpeechRate: 4,
                        WindowsSpeechVolume: 61,
                        KokoroVoice: KokoroSpeechSettings.DefaultVoice,
                        SpeechSpeed: KokoroSpeechSettings.DefaultSpeed,
                        SpeechVolume: KokoroSpeechSettings.DefaultVolume
                    },
                    "Windows voice, rate, and volume did not save independently from Kokoro settings.");

                backendBox.OpenDropDown();
                backendBox.CommitSelection(0);
                Application.DoEvents();
                Assert(
                    voiceBox.SelectedItem is KokoroVoiceDescriptor { Id: KokoroSpeechSettings.DefaultVoice }
                    && speedSlider.Minimum == 75
                    && speedSlider.Maximum == 125
                    && speedSlider.Value == 100
                    && volumeSlider.Value == 100,
                    "Switching back to Kokoro did not restore its independent controls.");
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw new InvalidOperationException(
                "The settings form did not expose working Windows voice controls.",
                failure);
        }
    }

    private static void CheckWindowCornerUsesPartialAlpha()
    {
        Assert(
            SettingsWindowChrome.CornerRadius == 19,
            "The settings chrome lost its refined outer corner radius.");
        Assert(
            SettingsWindowChrome.OuterBorderRadius == 15.5f
            && SettingsWindowChrome.InnerBorderRadius == 13.5f,
            "The outer corner refinement changed the established blue border curves.");
        using var bitmap = SettingsWindowChrome.RenderChrome(new Size(40, 40));

        var hasPartialAlpha = false;
        for (var y = 0; y < bitmap.Height && !hasPartialAlpha; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var alpha = bitmap.GetPixel(x, y).A;
                if (alpha is > 0 and < 255)
                {
                    hasPartialAlpha = true;
                    break;
                }
            }
        }

        Assert(
            hasPartialAlpha,
            "The settings window corner still uses a binary region without anti-aliased alpha.");
        Assert(
            bitmap.GetPixel(0, 0).A == 0
            && bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2).A == 255,
            "The anti-aliased settings chrome does not preserve transparent corners and opaque content.");

        using var composed = SettingsWindowChrome.RenderChrome(new Size(40, 40));
        using (var graphics = Graphics.FromImage(composed))
        using (var contentPath = PulseReaderTheme.RoundedRectangle(
            Rectangle.Inflate(
                new Rectangle(Point.Empty, composed.Size),
                -SettingsWindowChrome.ContentInset,
                -SettingsWindowChrome.ContentInset),
            SettingsWindowChrome.CornerRadius - SettingsWindowChrome.ContentInset))
        using (var contentRegion = new Region(contentPath))
        using (var contentFill = new SolidBrush(PulseReaderTheme.Window))
        {
            graphics.FillRegion(contentFill, contentRegion);
        }

        var centerX = composed.Width / 2;
        var visibleBorderRows = Enumerable.Range(0, 10)
            .Count(y =>
            {
                var pixel = composed.GetPixel(centerX, y);
                return pixel.A > 0 && pixel.ToArgb() != PulseReaderTheme.Window.ToArgb();
            });
        Assert(
            visibleBorderRows >= 5,
            "The settings content layer still masks most of the blue window border.");
    }

    private static void CheckStoppedPreviewIgnoresLateStart()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var previewStart = new TaskCompletionSource<SpeechStartResult>();
                using var form = new SettingsForm(
                    AppConfiguration.Default,
                    HotkeyDefinition.Default,
                    _ => null,
                    _ => null,
                    _ => previewStart.Task,
                    () => { },
                    () => null,
                    () => null);
                form.Show();
                Application.DoEvents();

                var viewport = form.Controls.OfType<SettingsScrollViewport>().Single();
                var previewButton = viewport.ContentControls
                    .OfType<ThemePanel>()
                    .SelectMany(panel => panel.Controls.OfType<GlowButton>())
                    .Single(button => button.Text.Contains("Preview", StringComparison.Ordinal));
                var statusLabel = viewport.ContentControls
                    .OfType<Label>()
                    .OrderByDescending(label => label.Top)
                    .First();

                previewButton.PerformClick();
                Application.DoEvents();
                previewButton.PerformClick();
                Application.DoEvents();
                Assert(statusLabel.Text == "Preview stopped.",
                    "Stopping a pending preview did not update the status.");

                previewStart.SetResult(SpeechStartResult.Success());
                Application.DoEvents();
                Assert(
                    previewButton.Text == "▶  Preview voice"
                    && statusLabel.Text == "Preview stopped.",
                    "A late preview start restored the playing state after Stop.");
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw new InvalidOperationException(
                "Stopping a voice preview did not remain authoritative.",
                failure);
        }
    }

    private static void CheckUnavailableContextualModelCanBeDisabled()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var enabledConfiguration = AppConfiguration.Default with
                {
                    ContextualPronunciationEnabled = true
                };
                AppConfiguration? appliedConfiguration = null;
                var unavailable = new ContextualPronunciationAvailability(
                    false,
                    "test",
                    "Optional model is unavailable for this test.");

                using (var form = new SettingsForm(
                           enabledConfiguration,
                           HotkeyDefinition.Default,
                           configuration =>
                           {
                               appliedConfiguration = configuration;
                               return null;
                           },
                           _ => null,
                           _ => Task.FromResult(SpeechStartResult.Success()),
                           () => { },
                           () => null,
                           () => null,
                           contextualPronunciationAvailability: unavailable))
                {
                    form.Show();
                    Application.DoEvents();

                    var checkBox = form.Controls
                        .OfType<SettingsScrollViewport>()
                        .Single()
                        .ContentControls
                        .OfType<ThemePanel>()
                        .SelectMany(panel => panel.Controls.OfType<GlowCheckBox>())
                        .Single(candidate =>
                            candidate.Text == "Optional local model is not installed");
                    Assert(
                        checkBox.Enabled && checkBox.Checked,
                        "An unavailable contextual model trapped an already-enabled setting in a disabled state.");

                    checkBox.Checked = false;
                    Application.DoEvents();
                    Assert(
                        appliedConfiguration is { ContextualPronunciationEnabled: false }
                        && !checkBox.Checked,
                        "The contextual-pronunciation setting could not be disabled after its model disappeared.");
                }

                using var disabledForm = new SettingsForm(
                    AppConfiguration.Default,
                    HotkeyDefinition.Default,
                    _ => null,
                    _ => null,
                    _ => Task.FromResult(SpeechStartResult.Success()),
                    () => { },
                    () => null,
                    () => null,
                    contextualPronunciationAvailability: unavailable);
                disabledForm.Show();
                Application.DoEvents();

                var unavailableCheckBox = disabledForm.Controls
                    .OfType<SettingsScrollViewport>()
                    .Single()
                    .ContentControls
                    .OfType<ThemePanel>()
                    .SelectMany(panel => panel.Controls.OfType<GlowCheckBox>())
                    .Single(candidate =>
                        candidate.Text == "Optional local model is not installed");
                Assert(
                    !unavailableCheckBox.Enabled && !unavailableCheckBox.Checked,
                    "An unavailable contextual model could be enabled from a disabled saved state.");
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw new InvalidOperationException(
                "Unavailable contextual model settings did not recover cleanly.",
                failure);
        }
    }

    private static void CheckSettingsFormStateAndLayout()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var registeredHotkey = HotkeyDefinition.Default;
                AppConfiguration? appliedConfiguration = null;
                bool? appliedStartupSetting = null;
                var failStartupChange = false;
                using var form = new SettingsForm(
                    AppConfiguration.Default,
                    registeredHotkey,
                    configuration =>
                    {
                        appliedConfiguration = configuration;
                        return null;
                    },
                    _ => null,
                    _ => Task.FromResult(SpeechStartResult.Success()),
                    () => { },
                    () => null,
                    () => null,
                    startWithWindows: false,
                    applyStartupRegistration: enabled =>
                    {
                        appliedStartupSetting = enabled;
                        return failStartupChange ? "Startup test failure." : null;
                    },
                    contextualPronunciationAvailability:
                        new ContextualPronunciationAvailability(true, "test", null));
                form.Show();
                Application.DoEvents();
                form.ApplyWorkingAreaForTest(new Rectangle(0, 0, 1920, 1200));
                Application.DoEvents();

                Assert(
                    form.ClientSize == new Size(416, 915),
                    "The borderless form retained hidden frame space on the right or bottom.");
                Assert(
                    form.Region is not null && !form.Region.IsVisible(2, 2),
                    "The settings window is not using the larger custom corner radius.");

                var viewport = form.Controls.OfType<SettingsScrollViewport>().Single();
                var themePanels = viewport.ContentControls
                    .OfType<ThemePanel>()
                    .OrderBy(panel => panel.Top)
                    .ToArray();
                var voicePanel = themePanels.First();
                var startupPanel = themePanels.Single(panel =>
                    panel.Controls.OfType<GlowCheckBox>().Any(checkBox =>
                        checkBox.Text == "Start Pulse Reader when I sign in"));
                var startupCheckBox = startupPanel.Controls.OfType<GlowCheckBox>().Single();
                var playbackPanel = themePanels.Single(panel =>
                    panel.Controls.OfType<GlowCheckBox>().Any(checkBox =>
                        checkBox.Text == "Show playback and monitoring controls"));
                var playbackCheckBox = playbackPanel.Controls.OfType<GlowCheckBox>().Single();
                var contextualPanel = themePanels.Single(panel =>
                    panel.Controls.OfType<GlowCheckBox>().Any(checkBox =>
                        checkBox.Text == "Use local model for ambiguous words"));
                var contextualCheckBox = contextualPanel.Controls.OfType<GlowCheckBox>().Single();
                Assert(
                    viewport.Top == 72 && voicePanel.Top == 0,
                    "The voice panel still leaves too much space below the fixed header.");
                Assert(
                    !viewport.ScrollBarVisible,
                    "The settings viewport shows a scrollbar when the compact layout already fits.");
                var resetButtons = voicePanel.Controls
                    .OfType<GlowButton>()
                    .Where(button => button.Text == "Reset")
                    .OrderBy(button => button.Top)
                    .ToArray();
                var valueLabels = voicePanel.Controls
                    .OfType<Label>()
                    .Where(label => label.Size == new Size(44, 20))
                    .OrderBy(label => label.Top)
                    .ToArray();
                var previewButton = voicePanel.Controls
                    .OfType<GlowButton>()
                    .Single(button => button.Text.Contains("Preview", StringComparison.Ordinal));

                Assert(
                    resetButtons.Length == 2 && resetButtons[0].Left == resetButtons[1].Left,
                    "The speed and volume reset buttons do not share a column.");
                Assert(
                    valueLabels.Length == 2 && valueLabels[0].Left == valueLabels[1].Left,
                    "The speed and volume values do not share a column.");
                Assert(
                    voicePanel.Height - previewButton.Bottom == previewButton.Left,
                    "The voice panel bottom padding does not match its side padding.");
                contextualCheckBox.Checked = true;
                Application.DoEvents();
                Assert(
                    appliedConfiguration is { ContextualPronunciationEnabled: true }
                    && contextualCheckBox.Checked,
                    "The contextual-pronunciation setting was not saved immediately.");
                playbackCheckBox.Checked = false;
                Application.DoEvents();
                Assert(
                    appliedConfiguration is { ShowPlaybackControls: false }
                    && !playbackCheckBox.Checked,
                    "The floating playback control setting was not saved immediately.");
                startupCheckBox.Checked = true;
                Application.DoEvents();
                Assert(
                    appliedStartupSetting == true && startupCheckBox.Checked,
                    "The startup checkbox did not apply an enabled registration immediately.");
                failStartupChange = true;
                startupCheckBox.Checked = false;
                Application.DoEvents();
                var statusLabel = viewport.ContentControls.OfType<Label>().Single();
                Assert(
                    appliedStartupSetting == false
                    && startupCheckBox.Checked
                    && statusLabel.Text == "Startup test failure."
                    && statusLabel.ForeColor == PulseReaderTheme.Error,
                    "A failed startup change did not restore the checkbox and display its error.");

                var blendHeader = voicePanel.Controls.OfType<BlendSectionHeader>().Single();
                var blendCheckBox = voicePanel.Controls.OfType<GlowCheckBox>().Single();
                var secondaryLanguageBox = voicePanel.Controls
                    .OfType<ThemedComboBox>()
                    .Single(box => box.Top == 295);
                var secondaryVoiceBox = voicePanel.Controls
                    .OfType<ThemedComboBox>()
                    .Single(box => box.Top == 359);
                var blendSeparator = voicePanel.Controls
                    .OfType<Panel>()
                    .Where(panel => panel.Height == 1)
                    .Single();
                Assert(
                    !blendHeader.Expanded
                    && blendHeader.Visible
                    && !blendCheckBox.Visible
                    && !secondaryLanguageBox.Visible
                    && !secondaryVoiceBox.Visible
                    && !blendSeparator.Visible,
                    "The collapsed blend divider did not hide its optional controls.");

                blendHeader.Expanded = true;
                Application.DoEvents();
                Assert(
                    form.ClientSize == new Size(416, 1150)
                    && voicePanel.Height == 652
                    && blendCheckBox.Visible
                    && secondaryLanguageBox.Visible
                    && secondaryVoiceBox.Visible
                    && blendSeparator.Visible,
                    "The blend divider did not expand and reveal the second voice controls.");
                var blendSlider = voicePanel.Controls
                    .OfType<GlowSlider>()
                    .Single(slider => slider.Top == 422);
                var secondaryLanguageLabel = voicePanel.Controls
                    .OfType<Label>()
                    .Single(label => label.Text == "LANGUAGE 2");
                var secondaryVoiceLabel = voicePanel.Controls
                    .OfType<Label>()
                    .Single(label => label.Text == "VOICE 2");
                var blendBalanceLabel = voicePanel.Controls
                    .OfType<Label>()
                    .Single(label => label.Text == "VOICE BALANCE");
                var speedLabel = voicePanel.Controls
                    .OfType<Label>()
                    .Single(label => label.Text == "SPEED");
                Assert(
                    secondaryLanguageBox.SelectedItem as string == "American English"
                    && secondaryVoiceBox.Items.Cast<KokoroVoiceDescriptor>()
                        .All(voice => voice.Language == "American English"),
                    "Voice 2 is not filtered through its own language selection.");
                Assert(
                    !blendCheckBox.Checked
                    && !secondaryLanguageBox.Enabled
                    && !secondaryVoiceBox.Enabled
                    && blendSeparator.Top > blendSlider.Bottom,
                    "Opening the blend section enabled Voice 2 before its checkbox was selected.");
                Assert(
                    blendCheckBox.Top - blendHeader.Bottom == 7
                    && secondaryLanguageLabel.Top - blendCheckBox.Bottom == 11
                    && secondaryVoiceLabel.Top - secondaryLanguageBox.Bottom == 11
                    && blendBalanceLabel.Top - secondaryVoiceBox.Bottom == 11
                    && blendSeparator.Top - blendSlider.Bottom == 11
                    && speedLabel.Top - blendSeparator.Bottom == 15,
                    "The blend section does not preserve its intended compact top and relaxed bottom spacing.");
                blendCheckBox.Checked = true;
                blendSlider.Value = 65;
                Application.DoEvents();
                Assert(
                    appliedConfiguration is
                    {
                        KokoroVoiceBlendEnabled: true,
                        KokoroSecondaryVoice: KokoroSpeechSettings.DefaultSecondaryVoice,
                        KokoroPrimaryVoiceWeight: 65
                    },
                    "The settings menu did not persist the selected two-voice blend.");
                Assert(
                    voicePanel.Height - previewButton.Bottom == previewButton.Left,
                    "The expanded voice panel bottom padding does not match its side padding.");

                Assert(
                    SettingsForm.ConstrainClientHeight(1150, 1200) == 1150
                    && SettingsForm.ConstrainClientHeight(1150, 728) == 704
                    && SettingsForm.ConstrainClientHeight(1070, 1040) == 1016
                    && SettingsForm.ConstrainClientHeight(915, 600) == 576,
                    "Settings height constraints do not preserve 12-pixel working-area margins.");
                form.ApplyWorkingAreaForTest(new Rectangle(0, 0, 1366, 728));
                Application.DoEvents();
                var themedScrollBar = viewport.Controls.OfType<SettingsScrollBar>().Single();
                Assert(
                    form.ClientSize == new Size(416, 704)
                    && viewport.ScrollBarVisible
                    && themedScrollBar.Visible
                    && themedScrollBar.BackColor == PulseReaderTheme.DisabledSurface
                    && viewport.MaximumScrollOffset == 446,
                    "The expanded settings did not switch to the themed constrained-height viewport.");
                viewport.EnsureVisible(startupCheckBox);
                Application.DoEvents();
                Assert(
                    viewport.ScrollOffset > 0
                    && viewport.IsControlFullyVisible(startupCheckBox),
                    "Scrolling did not make the off-screen startup control fully reachable.");
                form.ApplyWorkingAreaForTest(new Rectangle(0, 0, 1920, 1200));
                Application.DoEvents();
                Assert(
                    form.ClientSize == new Size(416, 1150)
                    && !viewport.ScrollBarVisible
                    && viewport.ScrollOffset == 0,
                    "Restoring a taller working area did not return the approved full-height layout.");

                blendHeader.Expanded = false;
                Application.DoEvents();
                Assert(
                    form.ClientSize == new Size(416, 915)
                    && blendCheckBox.Checked
                    && !blendCheckBox.Visible
                    && !secondaryLanguageBox.Visible
                    && !secondaryVoiceBox.Visible,
                    "Collapsing the blend section changed its setting or left controls visible.");

                var editedHotkey = new HotkeyDefinition(
                    HotkeyModifiers.Control | HotkeyModifiers.Shift,
                    Keys.F8);
                var hotkeyBox = viewport.ContentControls
                    .OfType<ThemePanel>()
                    .SelectMany(panel => panel.Controls.OfType<HotkeyCaptureBox>())
                    .Single();
                hotkeyBox.Focus();
                Application.DoEvents();
                Assert(hotkeyBox.Focused,
                    "The hotkey edit setup did not enter capture focus.");
                Assert(hotkeyBox.TryCapture(Keys.Control | Keys.Shift | Keys.F8),
                    "The hotkey edit setup did not capture the temporary shortcut.");
                form.Hide();
                Application.DoEvents();
                form.Show();
                Application.DoEvents();
                Assert(
                    hotkeyBox.Hotkey == registeredHotkey && hotkeyBox.Hotkey != editedHotkey,
                    "Closing settings did not discard the unapplied hotkey edit.");
                Assert(
                    !hotkeyBox.IsCapturing && !hotkeyBox.Focused,
                    $"Reopening settings left the hotkey box listening for key presses. "
                    + $"Capturing={hotkeyBox.IsCapturing}, Focused={hotkeyBox.Focused}.");
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw new InvalidOperationException(
                "The settings form did not preserve its layout and close semantics.",
                failure);
        }
    }

    private static void CheckPickerSelectionClosesCleanly()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new Form { ShowInTaskbar = false };
                using var picker = new ThemedComboBox(
                    static control => control.PointToScreen(
                        new Point(control.Width / 2, control.Height / 2)))
                {
                    Size = new Size(240, 33)
                };
                picker.Items.AddRange(["Kokoro", "Windows"]);
                picker.SelectedItem = "Kokoro";
                form.Controls.Add(picker);
                form.Show();

                picker.OpenDropDown();
                var activeListField = typeof(ThemedComboBox).GetField(
                    "_activeList",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                Assert(
                    activeListField?.GetValue(picker) is Control activeList
                    && activeList is not ListBox,
                    "The themed picker still uses the native white ListBox scrollbar.");

                var toggleMethod = typeof(ThemedComboBox).GetMethod(
                    "ToggleDropDown",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                Assert(toggleMethod is not null,
                    "The themed picker does not expose a second-click toggle path.");
                toggleMethod!.Invoke(picker, null);
                Application.DoEvents();
                var activeDropDownField = typeof(ThemedComboBox).GetField(
                    "_activeDropDown",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                Assert(
                    activeDropDownField?.GetValue(picker) is null,
                    "Clicking an open themed picker did not close its popup.");

                picker.OpenDropDown();
                var autoClosingDropDown = activeDropDownField?.GetValue(picker) as ToolStripDropDown;
                Assert(autoClosingDropDown is not null,
                    "The themed picker did not open for the rapid-click check.");
                autoClosingDropDown!.Close(ToolStripDropDownCloseReason.AppClicked);

                toggleMethod.Invoke(picker, null);
                Application.DoEvents();
                Assert(
                    activeDropDownField?.GetValue(picker) is null,
                    "The click that closed the popup immediately reopened it.");

                picker.OpenDropDown();
                autoClosingDropDown = activeDropDownField?.GetValue(picker) as ToolStripDropDown;
                Assert(autoClosingDropDown is not null,
                    "The themed picker did not reopen for the next-click check.");
                autoClosingDropDown!.Close(ToolStripDropDownCloseReason.AppClicked);

                var mouseUpMethod = typeof(ThemedComboBox).GetMethod(
                    "OnMouseUp",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                Assert(mouseUpMethod is not null,
                    "The themed picker does not complete its popup click gesture.");
                mouseUpMethod!.Invoke(
                    picker,
                    [new MouseEventArgs(MouseButtons.Left, 1, picker.Width / 2, picker.Height / 2, 0)]);
                Application.DoEvents();

                toggleMethod.Invoke(picker, null);
                Application.DoEvents();
                Assert(
                    activeDropDownField?.GetValue(picker) is ToolStripDropDown { Visible: true },
                    "The first click after an automatic popup close was ignored.");

                toggleMethod.Invoke(picker, null);
                Application.DoEvents();
                var doubleClickMethod = typeof(ThemedComboBox).GetMethod(
                    "OnDoubleClick",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                Assert(doubleClickMethod is not null,
                    "The themed picker does not expose a rapid second-click path.");
                doubleClickMethod!.Invoke(picker, [EventArgs.Empty]);
                Application.DoEvents();
                Assert(
                    activeDropDownField?.GetValue(picker) is ToolStripDropDown { Visible: true },
                    "A rapid second click did not toggle the themed picker.");

                toggleMethod.Invoke(picker, null);
                Application.DoEvents();
                picker.OpenDropDown();
                picker.CommitSelection(1);
                Application.DoEvents();

                Assert(
                    Equals(picker.SelectedItem, "Windows"),
                    "The themed picker did not commit its selected item.");
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw new InvalidOperationException(
                "Selecting a themed picker item did not close cleanly.",
                failure);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
