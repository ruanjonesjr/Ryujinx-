using global::Android.App;
using global::Android.Content;
using global::Android.OS;
using global::Android.Views;
using global::Android.Widget;
using global::Android.Runtime;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Vulkan;
using Ryujinx.HLE;
using Ryujinx.HLE.FileSystem;
using Ryujinx.HLE.HOS;
using Ryujinx.HLE.HOS.Services.Account.Acc;
using Ryujinx.HLE.HOS.SystemState;
using Ryujinx.HLE.HOS.Services.Hid;
using RyujinxSwitch = Ryujinx.HLE.Switch;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Gpu;
using LibHac.Tools.FsSystem;
using Silk.NET.Vulkan;
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.Android
{
    [Activity(Label = "Ryujinx Android", Exported = true,
        ConfigurationChanges = global::Android.Content.PM.ConfigChanges.Orientation |
                              global::Android.Content.PM.ConfigChanges.ScreenSize |
                              global::Android.Content.PM.ConfigChanges.KeyboardHidden,
        ScreenOrientation = global::Android.Content.PM.ScreenOrientation.SensorLandscape)]
    public sealed class MainActivity : Activity, ISurfaceHolderCallback
    {
        private const int PickGameRequest = 10;
        private const int PickKeysRequest = 11;
        private const int PickFirmwareRequest = 12;

        private SurfaceView _surfaceView;
        private TextView _status;
        private FrameLayout _rootLayout;
        private LinearLayout _settingsPage;
        private ProgressBar _importProgress;
        private int _autoStartRequested;
        private string _previousVulkanStage;
        private LinearLayout _bottomPanel;
        private LinearLayout _topPanel;
        private LinearLayout _gameList;
        private volatile bool _isImporting;
        private volatile AndroidHost _host;
        private string _pendingAction;
        private volatile bool _surfaceReady;
        private volatile bool _vulkanRequested;
        private ISurfaceHolder _surfaceHolder;
        private int _surfaceGeneration;
        private int _surfaceWidth;
        private int _surfaceHeight;
        private long _pendingNativeWindow;
        private int _hostInitializationInProgress;

        protected override void OnCreate(Bundle savedInstanceState)
        {
            try
            {
                base.OnCreate(savedInstanceState);
                AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                {
                    try
                    {
                        string logPath = Path.Combine(FilesDir.AbsolutePath, "ryujinx-startup-crash.txt");
                        File.AppendAllText(logPath, DateTime.UtcNow.ToString("O") + System.Environment.NewLine +
                            (args.ExceptionObject?.ToString() ?? "Falha não gerenciada") + System.Environment.NewLine);
                    }
                    catch { }
                };
                Window.AddFlags(WindowManagerFlags.KeepScreenOn);

            var root = new FrameLayout(this);
            _rootLayout = root;

            _surfaceView = new SurfaceView(this);
            _surfaceView.Holder.AddCallback(this);
            root.AddView(_surfaceView, new FrameLayout.LayoutParams(-1, -1));

            root.SetBackgroundColor(global::Android.Graphics.Color.Rgb(239, 240, 242));
            _topPanel = new LinearLayout(this) { Orientation = Orientation.Vertical };
            _topPanel.SetBackgroundColor(global::Android.Graphics.Color.Rgb(239, 240, 242));
            _topPanel.SetPadding(22, 18, 22, 14);

            var topBar = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            topBar.SetGravity(GravityFlags.CenterVertical);
            var brand = new TextView(this) { Text = "RYUJINX", TextSize = 25, Gravity = GravityFlags.CenterVertical };
            brand.SetTextColor(global::Android.Graphics.Color.Rgb(35, 42, 52));
            brand.SetTypeface(null, global::Android.Graphics.TypefaceStyle.Bold);
            topBar.AddView(brand, new LinearLayout.LayoutParams(0, 48, 1));
            var settingsButton = new Button(this) { Text = "⚙" };
            settingsButton.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 20f);
            settingsButton.Click += (_, _) => ShowSettingsPage();
            topBar.AddView(settingsButton, new LinearLayout.LayoutParams(54, 48));
            _topPanel.AddView(topBar);

            var subtitle = new TextView(this) { Text = "SUA BIBLIOTECA DE JOGOS", TextSize = 11, Gravity = GravityFlags.CenterVertical };
            subtitle.SetTextColor(global::Android.Graphics.Color.Rgb(105, 115, 128));
            _topPanel.AddView(subtitle, new LinearLayout.LayoutParams(-1, 28));

            var libraryHeading = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            libraryHeading.SetGravity(GravityFlags.CenterVertical);
            var libraryTitle = new TextView(this) { Text = "JOGOS", TextSize = 17, Gravity = GravityFlags.CenterVertical };
            libraryTitle.SetTextColor(global::Android.Graphics.Color.Rgb(42, 48, 58));
            libraryTitle.SetTypeface(null, global::Android.Graphics.TypefaceStyle.Bold);
            libraryHeading.AddView(libraryTitle, new LinearLayout.LayoutParams(0, 42, 1));
            var addGameButton = new Button(this) { Text = "+ ADICIONAR" };
            addGameButton.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 10f);
            addGameButton.Click += (_, _) => PickFile(PickGameRequest, "*/*", "game");
            libraryHeading.AddView(addGameButton, new LinearLayout.LayoutParams(-2, 40));
            _topPanel.AddView(libraryHeading);

            var libraryScroll = new HorizontalScrollView(this) { HorizontalScrollBarEnabled = false };
            _gameList = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            libraryScroll.AddView(_gameList, new global::Android.Views.ViewGroup.LayoutParams(-2, -1));
            _topPanel.AddView(libraryScroll, new LinearLayout.LayoutParams(-1, 218));

            _status = new TextView(this)
            {
                Text = "Biblioteca pronta",
                Gravity = GravityFlags.CenterVertical,
                TextSize = 11
            };
            _status.SetTextColor(global::Android.Graphics.Color.Rgb(105, 115, 128));
            _topPanel.AddView(_status, new LinearLayout.LayoutParams(-1, 30));
            _importProgress = new ProgressBar(this, null, global::Android.Resource.Attribute.ProgressBarStyleHorizontal)
            {
                Max = 100,
                Progress = 0,
                Visibility = global::Android.Views.ViewStates.Gone
            };
            _topPanel.AddView(_importProgress, new LinearLayout.LayoutParams(-1, 4));

            var flexibleSpace = new View(this);
            _topPanel.AddView(flexibleSpace, new LinearLayout.LayoutParams(1, 0, 1));

            var separator = new View(this);
            separator.SetBackgroundColor(global::Android.Graphics.Color.Rgb(195, 199, 205));
            _topPanel.AddView(separator, new LinearLayout.LayoutParams(-1, 1));

            var nav = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            nav.SetGravity(GravityFlags.Center);
            AddHomeAction(nav, "🎮", "JOGOS", () => { _status.Text = "Biblioteca de jogos"; });
            AddHomeAction(nav, "📁", "ARQUIVOS", () => PickFile(PickGameRequest, "*/*", "game"));
            AddHomeAction(nav, "🔑", "PROD.KEYS", () => PickFile(PickKeysRequest, "*/*", "keys"));
            AddHomeAction(nav, "📦", "FIRMWARE", () => PickFile(PickFirmwareRequest, "application/zip", "firmware"));
            AddHomeAction(nav, "⚙", "AJUSTES", () => ShowSettingsPage());
            _topPanel.AddView(nav, new LinearLayout.LayoutParams(-1, 92));

            var topParams = new FrameLayout.LayoutParams(-1, -1, GravityFlags.Fill);
            root.AddView(_topPanel, topParams);

            _bottomPanel = new LinearLayout(this) { Orientation = Orientation.Vertical };
            _bottomPanel.SetBackgroundColor(global::Android.Graphics.Color.Argb(150, 20, 22, 26));
            _bottomPanel.Visibility = global::Android.Views.ViewStates.Gone;
            var pad = new LinearLayout(this) { Orientation = Orientation.Vertical };

            var directions = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            AddPadButton(directions, "↑", ControllerKeys.DpadUp);
            AddPadButton(directions, "←", ControllerKeys.DpadLeft);
            AddPadButton(directions, "↓", ControllerKeys.DpadDown);
            AddPadButton(directions, "→", ControllerKeys.DpadRight);
            AddPadButton(directions, "−", ControllerKeys.Minus);
            AddPadButton(directions, "+", ControllerKeys.Plus);
            pad.AddView(directions, new LinearLayout.LayoutParams(-1, 38));

            var faceButtons = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            AddPadButton(faceButtons, "A", ControllerKeys.A);
            AddPadButton(faceButtons, "B", ControllerKeys.B);
            AddPadButton(faceButtons, "X", ControllerKeys.X);
            AddPadButton(faceButtons, "Y", ControllerKeys.Y);
            pad.AddView(faceButtons, new LinearLayout.LayoutParams(-1, 38));

            var shoulderButtons = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            AddPadButton(shoulderButtons, "L", ControllerKeys.L);
            AddPadButton(shoulderButtons, "R", ControllerKeys.R);
            AddPadButton(shoulderButtons, "ZL", ControllerKeys.Zl);
            AddPadButton(shoulderButtons, "ZR", ControllerKeys.Zr);
            AddPadButton(shoulderButtons, "L3", ControllerKeys.LStick);
            AddPadButton(shoulderButtons, "R3", ControllerKeys.RStick);
            AddPadButton(shoulderButtons, "SL", ControllerKeys.Sl);
            AddPadButton(shoulderButtons, "SR", ControllerKeys.Sr);
            pad.AddView(shoulderButtons, new LinearLayout.LayoutParams(-1, 38));
            _bottomPanel.AddView(pad);

            var sticks = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            AddStickControl(sticks, "ANALÓGICO ESQUERDO", true);
            AddStickControl(sticks, "ANALÓGICO DIREITO", false);
            _bottomPanel.AddView(sticks, new LinearLayout.LayoutParams(-1, 100));
            root.AddView(_bottomPanel, new FrameLayout.LayoutParams(-1, -2, GravityFlags.Bottom));

            SetContentView(root);
            Directory.CreateDirectory(Path.Combine(FilesDir.AbsolutePath, "Ryujinx"));
            _pendingGame = GetSharedPreferences("ryujinx_android", FileCreationMode.Private)
                .GetString("last_game", null);
            if (!string.IsNullOrEmpty(_pendingGame) && !File.Exists(_pendingGame))
            {
                _pendingGame = null;
                GetSharedPreferences("ryujinx_android", FileCreationMode.Private)
                    .Edit().Remove("last_game").Apply();
            }
            RefreshGameLibrary();
                string startupLog = Path.Combine(FilesDir.AbsolutePath, "Ryujinx", "android-vulkan-startup.log");
                if (File.Exists(startupLog))
                {
                    string[] previousLines = File.ReadAllLines(startupLog);
                    _previousVulkanStage = previousLines.Length > 0 ? previousLines[previousLines.Length - 1] : null;
                    _status.Text = "Biblioteca pronta";
                }
                else
                {
                    _status.Text = "Biblioteca pronta";
                }
            }
            catch (Exception ex)
            {
                try
                {
                    string logPath = Path.Combine(FilesDir.AbsolutePath, "ryujinx-startup-crash.txt");
                    File.WriteAllText(logPath, DateTime.UtcNow.ToString("O") + System.Environment.NewLine + ex);
                }
                catch { }

                var errorView = new TextView(this)
                {
                    Text = "RYUJINX — ERRO AO INICIAR\\n\\n" + ex.GetType().FullName + "\\n" + ex.Message +
                           "\\n\\nRelatório salvo em: Android/data/com.ryujinx.android/files/ryujinx-startup-crash.txt",
                    TextSize = 14,
                    Gravity = GravityFlags.Center,
                };
                errorView.SetTextColor(global::Android.Graphics.Color.White);
                errorView.SetBackgroundColor(global::Android.Graphics.Color.Rgb(35, 20, 20));
                SetContentView(errorView);
            }
        }

        private void ShowSettingsPage()
        {
            if (_settingsPage != null)
            {
                _settingsPage.Visibility = global::Android.Views.ViewStates.Visible;
                _topPanel.Visibility = global::Android.Views.ViewStates.Gone;
                return;
            }

            _settingsPage = new LinearLayout(this) { Orientation = Orientation.Vertical };
            _settingsPage.SetPadding(28, 22, 28, 22);
            _settingsPage.SetBackgroundColor(global::Android.Graphics.Color.Rgb(18, 22, 29));

            // Header fixo com navegação visível no topo da tela de configurações.
            var header = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            header.SetGravity(GravityFlags.CenterVertical);

            var backTop = new Button(this) { Text = "‹ VOLTAR" };
            backTop.Click += (_, _) => ShowHomePage();
            header.AddView(backTop, new LinearLayout.LayoutParams(-2, 48));

            var heading = new TextView(this)
            {
                Text = "CONFIGURAÇÕES",
                TextSize = 21,
                Gravity = GravityFlags.CenterVertical
            };
            heading.SetTextColor(global::Android.Graphics.Color.White);
            heading.SetTypeface(null, global::Android.Graphics.TypefaceStyle.Bold);
            var headingParams = new LinearLayout.LayoutParams(0, 54, 1);
            headingParams.LeftMargin = 12;
            header.AddView(heading, headingParams);
            _settingsPage.AddView(header, new LinearLayout.LayoutParams(-1, 58));

            var description = new TextView(this) { Text = "Desempenho e funcionamento do emulador", TextSize = 13 };
            description.SetTextColor(global::Android.Graphics.Color.Rgb(165, 177, 194));
            _settingsPage.AddView(description, new LinearLayout.LayoutParams(-1, 32));

            var preferences = GetSharedPreferences("ryujinx_android", FileCreationMode.Private);

            var vulkanCheck = new CheckBox(this)
            {
                Text = "Usar Vulkan (experimental)",
                Checked = preferences.GetBoolean("use_vulkan", false)
            };
            vulkanCheck.SetTextColor(global::Android.Graphics.Color.White);
            _settingsPage.AddView(vulkanCheck);

            var rendererInfo = new TextView(this)
            {
                Text = "O núcleo original do Ryujinx usa Vulkan. OpenGL não está implementado como renderizador neste porte Android.",
                TextSize = 12
            };
            rendererInfo.SetTextColor(global::Android.Graphics.Color.Rgb(165, 177, 194));
            _settingsPage.AddView(rendererInfo, new LinearLayout.LayoutParams(-1, -2));

            float[] scales = { 0.5f, 0.75f, 1.0f, 1.25f };
            string[] labels = { "0,5x — Desempenho", "0,75x — Equilibrado", "1,0x — Padrão", "1,25x — Qualidade" };
            float current = preferences.GetFloat("resolution_scale", 0.75f);
            int selected = Array.IndexOf(scales, current);
            if (selected < 0) selected = 1;

            var scaleLabel = new TextView(this) { Text = "Resolução interna", TextSize = 15 };
            scaleLabel.SetTextColor(global::Android.Graphics.Color.White);
            _settingsPage.AddView(scaleLabel, new LinearLayout.LayoutParams(-1, 34));

            var scaleSpinner = new Spinner(this);
            var adapter = new ArrayAdapter<string>(this, global::Android.Resource.Layout.SimpleSpinnerItem, labels);
            adapter.SetDropDownViewResource(global::Android.Resource.Layout.SimpleSpinnerDropDownItem);
            scaleSpinner.Adapter = adapter;
            scaleSpinner.SetSelection(selected);
            _settingsPage.AddView(scaleSpinner, new LinearLayout.LayoutParams(-1, 48));

            var vsyncCheck = new CheckBox(this) { Text = "VSync", Checked = preferences.GetBoolean("vsync", true) };
            var dockedCheck = new CheckBox(this) { Text = "Modo Dock (maior carga gráfica)", Checked = preferences.GetBoolean("docked_mode", false) };
            vsyncCheck.SetTextColor(global::Android.Graphics.Color.White);
            dockedCheck.SetTextColor(global::Android.Graphics.Color.White);
            _settingsPage.AddView(vsyncCheck);
            _settingsPage.AddView(dockedCheck);

            var apply = new Button(this) { Text = "SALVAR CONFIGURAÇÕES" };
            apply.Click += async (_, _) =>
            {
                float scale = scales[Math.Max(0, scaleSpinner.SelectedItemPosition)];
                bool vsync = vsyncCheck.Checked;
                bool docked = dockedCheck.Checked;
                bool useVulkan = vulkanCheck.Checked;
                preferences.Edit()
                    .PutBoolean("use_vulkan", useVulkan)
                    .PutFloat("resolution_scale", scale)
                    .PutBoolean("vsync", vsync)
                    .PutBoolean("docked_mode", docked)
                    .Apply();
                GraphicsConfig.ResScale = scale;
                _host?.ApplySettings(scale, vsync, docked);
                ShowHomePage();

                if (useVulkan)
                {
                    _vulkanRequested = true;
                    if (_surfaceReady) StartVulkanHost();
                    else _status.Text = "Vulkan ativado. Aguarde a superfície Android ficar pronta.";
                }
                else
                {
                    _vulkanRequested = false;
                    await StopVulkanHostAsync();
                    _status.Text = "Vulkan desativado. O aplicativo permanece na biblioteca.";
                }
            };
            _settingsPage.AddView(apply, new LinearLayout.LayoutParams(-1, 48));

            var back = new Button(this) { Text = "VOLTAR À BIBLIOTECA" };
            back.Click += (_, _) => ShowHomePage();
            _settingsPage.AddView(back, new LinearLayout.LayoutParams(-1, 48));

            _rootLayout.AddView(_settingsPage, new FrameLayout.LayoutParams(-1, -1));
            _topPanel.Visibility = global::Android.Views.ViewStates.Gone;
        }

        private void ShowHomePage()
        {
            if (_settingsPage != null) _settingsPage.Visibility = global::Android.Views.ViewStates.Gone;
            _topPanel.Visibility = global::Android.Views.ViewStates.Visible;
        }

        public override void OnBackPressed()
        {
            // O botão Voltar do Android retorna à biblioteca quando as configurações estão abertas.
            if (_settingsPage != null &&
                _settingsPage.Visibility == global::Android.Views.ViewStates.Visible)
            {
                ShowHomePage();
                return;
            }

            base.OnBackPressed();
        }

        public override bool OnKeyDown(global::Android.Views.Keycode keyCode, KeyEvent e)
        {
            ControllerKeys? key = MapPhysicalKey(keyCode);
            if (key.HasValue)
            {
                _host?.SetButton(key.Value, true);
                return true;
            }
            return base.OnKeyDown(keyCode, e);
        }

        public override bool OnKeyUp(global::Android.Views.Keycode keyCode, KeyEvent e)
        {
            ControllerKeys? key = MapPhysicalKey(keyCode);
            if (key.HasValue)
            {
                _host?.SetButton(key.Value, false);
                return true;
            }
            return base.OnKeyUp(keyCode, e);
        }

        private static ControllerKeys? MapPhysicalKey(global::Android.Views.Keycode keyCode)
        {
            return keyCode switch
            {
                global::Android.Views.Keycode.ButtonA => ControllerKeys.B,
                global::Android.Views.Keycode.ButtonB => ControllerKeys.A,
                global::Android.Views.Keycode.ButtonX => ControllerKeys.Y,
                global::Android.Views.Keycode.ButtonY => ControllerKeys.X,
                global::Android.Views.Keycode.ButtonL1 => ControllerKeys.L,
                global::Android.Views.Keycode.ButtonR1 => ControllerKeys.R,
                global::Android.Views.Keycode.ButtonL2 => ControllerKeys.Zl,
                global::Android.Views.Keycode.ButtonR2 => ControllerKeys.Zr,
                global::Android.Views.Keycode.ButtonStart => ControllerKeys.Plus,
                global::Android.Views.Keycode.ButtonSelect => ControllerKeys.Minus,
                global::Android.Views.Keycode.ButtonThumbl => ControllerKeys.LStick,
                global::Android.Views.Keycode.ButtonThumbr => ControllerKeys.RStick,
                global::Android.Views.Keycode.DpadUp => ControllerKeys.DpadUp,
                global::Android.Views.Keycode.DpadDown => ControllerKeys.DpadDown,
                global::Android.Views.Keycode.DpadLeft => ControllerKeys.DpadLeft,
                global::Android.Views.Keycode.DpadRight => ControllerKeys.DpadRight,
                _ => null
            };
        }

        public override bool OnGenericMotionEvent(MotionEvent e)
        {
            if (e.Action == MotionEventActions.Move && _host != null)
            {
                const float deadzone = 0.16f;
                float lx = ApplyDeadzone(e.GetAxisValue(global::Android.Views.Axis.X), deadzone);
                float ly = -ApplyDeadzone(e.GetAxisValue(global::Android.Views.Axis.Y), deadzone);
                float rx = ApplyDeadzone(e.GetAxisValue(global::Android.Views.Axis.Z), deadzone);
                float ry = -ApplyDeadzone(e.GetAxisValue(global::Android.Views.Axis.Rz), deadzone);
                _host.SetStick(true, (int)(lx * 32767), (int)(ly * 32767));
                _host.SetStick(false, (int)(rx * 32767), (int)(ry * 32767));
                return true;
            }
            return base.OnGenericMotionEvent(e);
        }

        private static float ApplyDeadzone(float value, float deadzone)
        {
            return Math.Abs(value) < deadzone ? 0f : Math.Clamp(value, -1f, 1f);
        }

        private void AddPadButton(LinearLayout row, string label, ControllerKeys key)
        {
            var button = new Button(this) { Text = label };
            button.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 10f);
            button.SetMinimumHeight(34);
            button.SetPadding(2, 0, 2, 0);
            button.Touch += (_, e) =>
            {
                if (e.Event.Action == MotionEventActions.Down) _host?.SetButton(key, true);
                else if (e.Event.Action == MotionEventActions.Up || e.Event.Action == MotionEventActions.Cancel) _host?.SetButton(key, false);
                e.Handled = true;
            };
            row.AddView(button, new LinearLayout.LayoutParams(0, -1, 1));
        }

        private void AddStickControl(LinearLayout row, string label, bool left)
        {
            var stick = new TextView(this)
            {
                Text = label + "\narraste para mover",
                Gravity = GravityFlags.Center,
                TextSize = 12
            };
            stick.SetBackgroundColor(global::Android.Graphics.Color.Rgb(48, 52, 59));
            stick.SetTextColor(global::Android.Graphics.Color.White);
            stick.Touch += (_, e) =>
            {
                if (e.Event.Action == MotionEventActions.Down || e.Event.Action == MotionEventActions.Move)
                {
                    float nx = Math.Clamp((e.Event.GetX() / Math.Max(1, stick.Width)) * 2f - 1f, -1f, 1f);
                    float ny = Math.Clamp(1f - (e.Event.GetY() / Math.Max(1, stick.Height)) * 2f, -1f, 1f);
                    _host?.SetStick(left, (int)(nx * 32767), (int)(ny * 32767));
                }
                else if (e.Event.Action == MotionEventActions.Up || e.Event.Action == MotionEventActions.Cancel)
                {
                    _host?.SetStick(left, 0, 0);
                }
                e.Handled = true;
            };
            var lp = new LinearLayout.LayoutParams(0, -1, 1) { LeftMargin = 8, RightMargin = 8, TopMargin = 8, BottomMargin = 8 };
            row.AddView(stick, lp);
        }

        private void PickFile(int request, string mime, string action)
        {
            _pendingAction = action;
            var intent = new Intent(Intent.ActionOpenDocument);
            intent.AddCategory(Intent.CategoryOpenable);
            intent.SetType(mime);
            StartActivityForResult(intent, request);
        }

        protected override void OnActivityResult(int requestCode, global::Android.App.Result resultCode, Intent data)
        {
            base.OnActivityResult(requestCode, resultCode, data);
            if (resultCode != global::Android.App.Result.Ok || data?.Data == null) return;

            string action = requestCode switch
            {
                PickKeysRequest => "keys",
                PickFirmwareRequest => "firmware",
                PickGameRequest => "game",
                _ => null
            };

            if (action != null)
                _ = ImportSelectedDocumentAsync(data.Data, action);
        }

        private async Task ImportSelectedDocumentAsync(global::Android.Net.Uri uri, string action)
        {
            try
            {
                if (_isImporting) throw new InvalidOperationException("Aguarde a importação atual terminar.");
                _isImporting = true;
                RunOnUiThread(() =>
                {
                    _importProgress.Progress = 0;
                    _importProgress.Visibility = global::Android.Views.ViewStates.Visible;
                    _status.Text = action == "game" ? "Importando jogo..." : action == "keys" ? "Importando chaves..." : "Importando firmware...";
                });

                string displayName = QueryDisplayName(uri) ?? "selected-file";
                string extension = Path.GetExtension(displayName);
                string safeName = Path.GetFileName(displayName);
                foreach (char invalid in Path.GetInvalidFileNameChars())
                    safeName = safeName.Replace(invalid, '_');

                if (action == "keys")
                {
                    string keyName = safeName.ToLowerInvariant();
                    safeName = keyName.StartsWith("prod") ? "prod.keys" :
                               keyName.StartsWith("title") ? "title.keys" :
                               keyName.StartsWith("console") ? "console.keys" : null;
                    if (safeName == null)
                        throw new InvalidOperationException("Selecione um arquivo prod.keys, title.keys ou console.keys.");
                }

                string targetDir = action == "keys"
                    ? Path.Combine(FilesDir.AbsolutePath, "Ryujinx", "system")
                    : action == "game"
                        ? Path.Combine(FilesDir.AbsolutePath, "Ryujinx", "games")
                        : Path.Combine(FilesDir.AbsolutePath, "Ryujinx", "imports");

                Directory.CreateDirectory(targetDir);

                string targetName = action == "game"
                    ? GetUniqueGameFileName(targetDir, safeName)
                    : safeName;
                string target = Path.Combine(targetDir, targetName);

                long totalBytes = QuerySize(uri);
                await Task.Run(async () =>
                {
                    using var input = ContentResolver.OpenInputStream(uri);
                    if (input == null) throw new IOException("Não foi possível abrir o arquivo selecionado.");
                    using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, true);
                    byte[] buffer = new byte[1024 * 1024];
                    long copied = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        await output.WriteAsync(buffer, 0, read);
                        copied += read;
                        if (totalBytes > 0)
                        {
                            int percent = (int)Math.Clamp(copied * 100 / totalBytes, 0, 100);
                            RunOnUiThread(() => _importProgress.Progress = percent);
                        }
                    }
                    await output.FlushAsync();
                });
                RunOnUiThread(() => _importProgress.Progress = 100);

                if (action == "game")
                {
                    string ext = Path.GetExtension(target).ToLowerInvariant();
                    if (ext != ".xci" && ext != ".nsp" && ext != ".pfs0" && ext != ".nca" && ext != ".nro")
                    {
                        File.Delete(target);
                        throw new InvalidOperationException("Formato não suportado. Selecione .XCI, .NSP, .PFS0, .NCA ou .NRO.");
                    }
                    GetSharedPreferences("ryujinx_android", FileCreationMode.Private)
                        .Edit().PutString("last_game", target).Apply();
                    RunOnUiThread(RefreshGameLibrary);
                }

                if (action == "keys")
                {
                    _host?.ReloadKeys();
                    _status.Text = "Chaves importadas. Selecione um jogo.";
                }
                else if (action == "firmware")
                {
                    AndroidHost host = _host;
                    if (host == null)
                    {
                        _pendingFirmware = target;
                        host = _host;
                        if (host != null && Interlocked.CompareExchange(ref _pendingFirmware, null, target) == target)
                            await InstallFirmwareAsync(host, target);
                    }
                    else
                    {
                        await InstallFirmwareAsync(host, target);
                    }
                }
                else if (action == "game")
                {
                    AndroidHost host = _host;
                    if (host == null)
                    {
                        _pendingGame = target;
                        host = _host;
                        if (host != null && Interlocked.CompareExchange(ref _pendingGame, null, target) == target)
                            await StartGame(target);
                    }
                    else
                    {
                        await StartGame(target);
                    }
                }
            }
            catch (Exception ex)
            {
                RunOnUiThread(() => _status.Text = "Falha na importação: " + ex.Message);
            }
            finally
            {
                _isImporting = false;
                RunOnUiThread(() => _importProgress.Visibility = global::Android.Views.ViewStates.Gone);
            }
        }

        private static string GetUniqueGameFileName(string directory, string fileName)
        {
            string candidate = fileName;
            string name = Path.GetFileNameWithoutExtension(fileName);
            string extension = Path.GetExtension(fileName);
            int suffix = 2;
            while (File.Exists(Path.Combine(directory, candidate)))
            {
                candidate = name + " (" + suffix++ + ")" + extension;
            }
            return candidate;
        }

        private void RefreshGameLibrary()
        {
            if (_gameList == null) return;
            _gameList.RemoveAllViews();
            string directory = Path.Combine(FilesDir.AbsolutePath, "Ryujinx", "games");
            Directory.CreateDirectory(directory);
            string[] supported = { ".xci", ".nsp", ".pfs0", ".nca", ".nro" };
            var games = Directory.GetFiles(directory)
                .Where(path => Array.Exists(supported, ext => string.Equals(Path.GetExtension(path), ext, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (games.Length == 0)
            {
                var emptyCard = new LinearLayout(this) { Orientation = Orientation.Vertical };
                emptyCard.SetGravity(GravityFlags.Center);
                emptyCard.SetPadding(18, 18, 18, 18);
                emptyCard.SetBackgroundColor(global::Android.Graphics.Color.White);
                var emptyIcon = new TextView(this) { Text = "＋", TextSize = 42, Gravity = GravityFlags.Center };
                emptyIcon.SetTextColor(global::Android.Graphics.Color.Rgb(155, 164, 175));
                emptyCard.AddView(emptyIcon, new LinearLayout.LayoutParams(-1, 112));
                var empty = new TextView(this) { Text = "ADICIONE SEU PRIMEIRO JOGO", TextSize = 11, Gravity = GravityFlags.Center };
                empty.SetTextColor(global::Android.Graphics.Color.Rgb(92, 103, 117));
                emptyCard.AddView(empty);
                emptyCard.Click += (_, _) => PickFile(PickGameRequest, "*/*", "game");
                _gameList.AddView(emptyCard, new LinearLayout.LayoutParams(190, -1) { RightMargin = 12 });
                return;
            }

            global::Android.Graphics.Color[] colors =
            {
                global::Android.Graphics.Color.Rgb(218, 231, 241),
                global::Android.Graphics.Color.Rgb(245, 226, 205),
                global::Android.Graphics.Color.Rgb(220, 233, 216),
                global::Android.Graphics.Color.Rgb(235, 220, 235)
            };

            for (int i = 0; i < games.Length; i++)
            {
                string path = games[i];
                string name = Path.GetFileNameWithoutExtension(path);
                var card = new LinearLayout(this) { Orientation = Orientation.Vertical };
                card.SetBackgroundColor(colors[i % colors.Length]);
                card.SetPadding(12, 12, 12, 10);
                var cover = new FrameLayout(this);
                cover.SetBackgroundColor(colors[(i + 1) % colors.Length]);
                var coverTitle = new TextView(this)
                {
                    Text = name,
                    TextSize = 16,
                    Gravity = GravityFlags.Center
                
                };
                coverTitle.SetTextColor(global::Android.Graphics.Color.Rgb(43, 51, 62));
                coverTitle.SetTypeface(null, global::Android.Graphics.TypefaceStyle.Bold);
                coverTitle.SetMaxLines(4);
                cover.AddView(coverTitle, new FrameLayout.LayoutParams(-1, -1));
                card.AddView(cover, new LinearLayout.LayoutParams(-1, 142));

                var title = new TextView(this)
                {
                    Text = name,
                    TextSize = 12,
                    Gravity = GravityFlags.CenterVertical
                
                    Ellipsize = global::Android.Text.TextUtils.TruncateAt.End
                };
                title.SetMaxLines(1);
                title.SetTextColor(global::Android.Graphics.Color.Rgb(42, 48, 58));
                title.SetTypeface(null, global::Android.Graphics.TypefaceStyle.Bold);
                card.AddView(title, new LinearLayout.LayoutParams(-1, 30));

                var size = new TextView(this)
                {
                    Text = (new FileInfo(path).Length / (1024d * 1024d)).ToString("0.0") + " MB  •  TOQUE PARA ABRIR",
                    TextSize = 9,
                    Gravity = GravityFlags.CenterVertical
                };
                size.SetTextColor(global::Android.Graphics.Color.Rgb(91, 101, 114));
                card.AddView(size);
                card.Click += async (_, _) => await StartGame(path);
                card.LongClick += (_, e) =>
                {
                    new AlertDialog.Builder(this)
                        .SetTitle("Remover jogo")
                        .SetMessage("Excluir " + Path.GetFileName(path) + " da biblioteca? Esta ação não pode ser desfeita.")
                        .SetNegativeButton("Cancelar", (sender, args) => { })
                        .SetPositiveButton("Excluir", (sender, args) =>
                        {
                            try
                            {
                                if (string.Equals(_pendingGame, path, StringComparison.Ordinal))
                                    _pendingGame = null;
                                var prefs = GetSharedPreferences("ryujinx_android", FileCreationMode.Private);
                                string lastGame = prefs.GetString("last_game", null);
                                var editor = prefs.Edit();
                                if (string.Equals(lastGame, path, StringComparison.Ordinal)) editor.Remove("last_game");
                                editor.Apply();
                                File.Delete(path);
                                RefreshGameLibrary();
                                _status.Text = "Jogo removido da biblioteca.";
                            }
                            catch (Exception ex)
                            {
                                _status.Text = "Não foi possível excluir: " + ex.Message;
                            }
                        })
                        .Show();
                    e.Handled = true;
                };
                _gameList.AddView(card, new LinearLayout.LayoutParams(190, -1) { RightMargin = 12 });
            }
        }

        private void AddHomeAction(LinearLayout row, string symbol, string label, Action action)
        {
            var button = new LinearLayout(this) { Orientation = Orientation.Vertical };
            button.SetGravity(GravityFlags.Center);
            var circle = new TextView(this)
            {
                Text = symbol,
                TextSize = 23,
                Gravity = GravityFlags.Center
            };
            circle.SetTextColor(global::Android.Graphics.Color.Rgb(74, 83, 96));
            circle.SetBackgroundColor(global::Android.Graphics.Color.White);
            button.AddView(circle, new LinearLayout.LayoutParams(54, 54));
            var caption = new TextView(this) { Text = label, TextSize = 8, Gravity = GravityFlags.Center };
            caption.SetTextColor(global::Android.Graphics.Color.Rgb(93, 103, 116));
            button.AddView(caption, new LinearLayout.LayoutParams(-1, 24));
            button.Click += (_, _) => action();
            row.AddView(button, new LinearLayout.LayoutParams(0, -1, 1));
        }

        private long QuerySize(global::Android.Net.Uri uri)
        {
            using var cursor = ContentResolver.Query(uri, null, null, null, null);
            if (cursor == null || !cursor.MoveToFirst()) return -1;
            int index = cursor.GetColumnIndex(global::Android.Provider.OpenableColumns.Size);
            return index >= 0 && !cursor.IsNull(index) ? cursor.GetLong(index) : -1;
        }

        private string _pendingGame;
        private string _pendingFirmware;

        private async Task InstallFirmwareAsync(AndroidHost host, string path)
        {
            try
            {
                _status.Text = "Instalando firmware...";
                await Task.Run(() => host.InstallFirmware(path));
                try { File.Delete(path); } catch { }
                if (_host == host) _status.Text = "Firmware instalado.";
            }
            catch (Exception ex)
            {
                if (_host == host) _status.Text = "Falha ao instalar firmware: " + ex.Message;
            }
        }

        private string QueryDisplayName(global::Android.Net.Uri uri)
        {
            using var cursor = ContentResolver.Query(uri, null, null, null, null);
            if (cursor == null || !cursor.MoveToFirst()) return null;
            int index = cursor.GetColumnIndex(global::Android.Provider.OpenableColumns.DisplayName);
            return index >= 0 ? cursor.GetString(index) : null;
        }

        private void WriteVulkanStage(string stage)
        {
            try
            {
                string directory = Path.Combine(FilesDir.AbsolutePath, "Ryujinx");
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "android-vulkan-startup.log");
                File.AppendAllText(path, DateTime.UtcNow.ToString("O") + " | " + stage + System.Environment.NewLine);
            }
            catch { }
        }

        public void SurfaceCreated(ISurfaceHolder holder)
        {
            // Defer JNI/ANativeWindow access until the Surface callback has returned.
            _surfaceHolder = holder;
            _surfaceReady = true;
            Interlocked.Increment(ref _surfaceGeneration);
            bool useVulkan = GetSharedPreferences("ryujinx_android", FileCreationMode.Private)
                .GetBoolean("use_vulkan", false);
            _status.Text = useVulkan
                ? "Tela pronta. Inicie o Vulkan em Configurações."
                : "Biblioteca pronta. Escolha o renderizador em Configurações.";
        }

        private async Task StopVulkanHostAsync()
        {
            AndroidHost host = _host;
            _host = null;
            if (host != null)
            {
                _status.Text = "Encerrando motor Vulkan...";
                await Task.Run(host.Stop);
            }
            if (!_vulkanRequested) _status.Text = "Motor Vulkan encerrado.";
        }

        private void StartVulkanHost()
        {
            _vulkanRequested = true;
            if (_host != null)
            {
                _status.Text = "Motor Vulkan já está inicializado.";
                return;
            }

            ISurfaceHolder holder = _surfaceHolder;
            if (!_surfaceReady || holder == null)
            {
                _status.Text = "Aguardando a superfície da tela. Tente novamente em instantes.";
                return;
            }

            // Prevent rapid repeated taps from constructing two Vulkan instances
            // against the same Android Surface.
            if (Interlocked.CompareExchange(ref _hostInitializationInProgress, 1, 0) != 0)
            {
                _status.Text = "O motor já está sendo inicializado. Aguarde...";
                return;
            }

            long nativeWindow = 0;
            try
            {
                WriteVulkanStage("JNI: convertendo Surface em ANativeWindow");
                nativeWindow = NativeFromSurface(JNIEnv.Handle, IntPtr.Zero, holder.Surface.Handle);
                WriteVulkanStage(nativeWindow == 0 ? "JNI: ANativeWindow nulo" : "JNI: ANativeWindow criado");
                if (nativeWindow == 0)
                    throw new InvalidOperationException("ANativeWindow não foi criado a partir da Surface Android.");

                int generation = Volatile.Read(ref _surfaceGeneration);
                Interlocked.Exchange(ref _pendingNativeWindow, nativeWindow);
                _status.Text = "Inicializando motor Vulkan...";
                _ = InitializeHostAsync(nativeWindow, generation);
            }
            catch (Exception ex)
            {
                if (nativeWindow != 0)
                {
                    Interlocked.CompareExchange(ref _pendingNativeWindow, 0, nativeWindow);
                    try { NativeReleaseWindow(nativeWindow); } catch { }
                }
                Interlocked.Exchange(ref _hostInitializationInProgress, 0);
                ReportVulkanError(ex);
            }
        }


        private async Task InitializeHostAsync(long nativeWindow, int generation)
        {
            AndroidHost host = null;
            try
            {
                WriteVulkanStage("HOST: iniciando construtor AndroidHost");
                host = await Task.Run(() => new AndroidHost(this, nativeWindow));
                WriteVulkanStage("HOST: construtor concluído");
                if (!_vulkanRequested || !_surfaceReady || generation != Volatile.Read(ref _surfaceGeneration))
                {
                    Interlocked.CompareExchange(ref _pendingNativeWindow, 0, nativeWindow);
                    await Task.Run(host.Stop);
                    return;
                }

                _host = host;
                Interlocked.CompareExchange(ref _pendingNativeWindow, 0, nativeWindow);
                host.Resize(_surfaceWidth, _surfaceHeight);
                _status.Text = "Motor ativo";

                string pendingFirmware = _pendingFirmware;
                if (!string.IsNullOrEmpty(pendingFirmware) &&
                    Interlocked.CompareExchange(ref _pendingFirmware, null, pendingFirmware) == pendingFirmware)
                    await InstallFirmwareAsync(host, pendingFirmware);

                string pending = _pendingGame;
                _pendingGame = null;
                if (!string.IsNullOrEmpty(pending)) await StartGame(pending);
            }
            catch (Exception ex)
            {
                WriteVulkanStage("EXCEÇÃO GERENCIADA: " + ex.GetType().Name + " - " + ex.Message);
                if (host != null)
                {
                    // If the host was already published before a resize or
                    // post-initialization operation failed, remove the stale
                    // reference before disposing it so the user can retry.
                    Interlocked.CompareExchange(ref _host, null, host);
                    await Task.Run(host.Stop);
                }
                else
                {
                    Interlocked.CompareExchange(ref _pendingNativeWindow, 0, nativeWindow);
                    NativeReleaseWindow(nativeWindow);
                }

                if (_surfaceReady && generation == Volatile.Read(ref _surfaceGeneration))
                    ReportVulkanError(ex);
            }
            finally
            {
                Interlocked.Exchange(ref _hostInitializationInProgress, 0);
            }
        }

        public void SurfaceChanged(ISurfaceHolder holder, global::Android.Graphics.Format format, int width, int height)
        {
            _surfaceWidth = width;
            _surfaceHeight = height;
            _host?.Resize(width, height);
        }

        public void SurfaceDestroyed(ISurfaceHolder holder)
        {
            _surfaceReady = false;
            _surfaceHolder = null;
            Interlocked.Increment(ref _surfaceGeneration);
            // InitializeHostAsync owns the pending native-window reference and
            // releases it after noticing this generation was invalidated.
            AndroidHost host = _host;
            _host = null;
            if (host != null) _ = Task.Run(host.Stop);
        }

        private async Task StartGame(string path)
        {
            AndroidHost host = _host;
            if (host == null)
            {
                _pendingGame = path;
                _status.Text = "Jogo selecionado. Ative Vulkan em Configurações para iniciar o motor.";
                return;
            }

            try
            {
                _status.Text = "Carregando jogo...";
                bool loaded = await Task.Run(() => host.LoadGame(path));
                if (_host != host) return;
                _status.Text = loaded ? "Jogo carregado." : "O Ryujinx não reconheceu o jogo. Confira keys e firmware.";
                if (loaded)
                {
                    _topPanel.Visibility = global::Android.Views.ViewStates.Gone;
                    _bottomPanel.Visibility = global::Android.Views.ViewStates.Visible;
                    host.StartRenderLoop();
                }
            }
            catch (Exception ex)
            {
                if (_host == host) _status.Text = "Falha ao carregar: " + ex.Message;
            }
        }

        private void ReportVulkanError(Exception ex)
        {
            string details = ex.ToString();
            try
            {
                string path = Path.Combine(FilesDir.AbsolutePath, "Ryujinx", "android-vulkan-error.log");
                File.AppendAllText(path, DateTime.UtcNow.ToString("O") + System.Environment.NewLine + details + System.Environment.NewLine + System.Environment.NewLine);
            }
            catch { }

            _status.Text = "Vulkan indisponível neste momento";
            _status.Click -= ShowVulkanErrorDetails;
            _status.Click += ShowVulkanErrorDetails;
            _lastVulkanError = details;
        }

        private string _lastVulkanError;

        private void ShowVulkanErrorDetails(object sender, EventArgs e)
        {
            new AlertDialog.Builder(this)
                .SetTitle("Diagnóstico Vulkan")
                .SetMessage(_lastVulkanError ?? "Sem detalhes disponíveis.")
                .SetPositiveButton("OK", (_, _) => { })
                .Show();
        }

        protected override void OnDestroy()
        {
            // Invalidate any Vulkan initialization still running on a worker thread.
            // InitializeHostAsync will dispose the host if this generation is stale.
            _surfaceReady = false;
            Interlocked.Increment(ref _surfaceGeneration);

            // The in-flight initialization task owns its ANativeWindow reference.
            // It will release it when it observes the invalidated surface generation.
            AndroidHost host = _host;
            _host = null;
            if (host != null) _ = Task.Run(host.Stop);
            base.OnDestroy();
        }

        [DllImport("ryujinx_android", EntryPoint = "Java_com_ryujinx_android_NativeWindowBridge_fromSurface")]
        private static extern long NativeFromSurface(IntPtr env, IntPtr clazz, IntPtr surface);

        [DllImport("ryujinx_android", EntryPoint = "ryujinx_android_release_window")]
        private static extern void NativeReleaseWindow(long window);

        private sealed class AndroidHost
        {
            private readonly MainActivity _activity;
            private readonly long _nativeWindow;
            private readonly VirtualFileSystem _vfs;
            private readonly ContentManager _content;
            private readonly LibHacHorizonManager _libHac;
            private readonly VulkanRenderer _renderer;
            private RyujinxSwitch _device;
            private CancellationTokenSource _renderCancellation;
            private Task _renderTask;
            private int _stopStarted;
            private ControllerKeys _buttons;
            private JoystickPosition _leftStick;
            private JoystickPosition _rightStick;

            public AndroidHost(MainActivity activity, long nativeWindow)
            {
                _activity = activity;
                _nativeWindow = nativeWindow;

                VulkanRenderer renderer = null;
                RyujinxSwitch device = null;
                try
                {
                    string data = Path.Combine(activity.FilesDir.AbsolutePath, "Ryujinx");
                    Directory.CreateDirectory(data);
                    AppDataManager.Initialize(data);
                    // Match the desktop/headless frontend: shader caching is enabled there.
                    // Without this, every launch recompiles shaders and can cause severe stutter.
                    var preferences = activity.GetSharedPreferences("ryujinx_android", FileCreationMode.Private);
                    bool enableVsync = preferences.GetBoolean("vsync", true);
                    bool enableDockedMode = preferences.GetBoolean("docked_mode", false);
                    GraphicsConfig.EnableShaderCache = true;
                    GraphicsConfig.ResScale = preferences.GetFloat("resolution_scale", 0.75f);

                    _vfs = VirtualFileSystem.CreateInstance();
                    _libHac = new LibHacHorizonManager();
                    _libHac.InitializeFsServer(_vfs);
                    _libHac.InitializeArpServer();
                    _libHac.InitializeBcatServer();
                    _libHac.InitializeSystemClients();
                    _content = new ContentManager(_vfs);
                    var accounts = new AccountManager(_libHac.RyujinxClient);
                    var persistence = new UserChannelPersistence();

                    activity.WriteVulkanStage("VULKAN: obtendo API Silk.NET");
                    Vk vk = Vk.GetApi();
                    activity.WriteVulkanStage("VULKAN: criando VulkanRenderer");
                    renderer = new VulkanRenderer(vk, new IntPtr(nativeWindow), "");
                    activity.WriteVulkanStage("VULKAN: chamando Initialize");
                    // Initialize the Vulkan renderer before constructing HLE.
                    // Ryujinx's GPU subsystem may query renderer capabilities during
                    // Switch construction, so initializing it afterwards can abort
                    // engine startup on Android.
                    renderer.Initialize(GraphicsDebugLevel.None);
                    activity.WriteVulkanStage("VULKAN: Initialize concluído");

                    activity.WriteVulkanStage("HLE: preparando configuração do Switch");
                    var configuration = new HLEConfiguration(
                        _vfs, _libHac, _content, accounts, persistence, renderer,
                        new AndroidAudioHardwareDeviceDriver(),
                        MemoryConfiguration.MemoryConfiguration4GiB,
                        new AndroidHostUIHandler(activity),
                        SystemLanguage.BrazilianPortuguese,
                        RegionCode.USA,
                        enableVsync, enableDockedMode, true, false,
                        IntegrityCheckLevel.None, 0, 0, "UTC",
                        MemoryManagerMode.HostMapped,
                        true, AspectRatio.Fixed16x9, 1.0f, false, "",
                        Ryujinx.Common.Configuration.Multiplayer.MultiplayerMode.Disabled);
                    device = new RyujinxSwitch(configuration);
                    activity.WriteVulkanStage("HLE: Switch criado");

                    _renderer = renderer;
                    _device = device;
                }
                catch
                {
                    if (device != null)
                    {
                        try { device.DisposeGpu(); } catch { }
                        try { device.Dispose(); } catch { }
                    }
                    else
                    {
                        try { renderer?.Dispose(); } catch { }
                    }
                    throw;
                }
            }

            public void ReloadKeys() => _vfs.ReloadKeySet();

            public void SetButton(ControllerKeys key, bool pressed)
            {
                if (pressed) _buttons |= key; else _buttons &= ~key;
                PushInput();
            }

            public void SetStick(bool left, int x, int y)
            {
                if (left) _leftStick = new JoystickPosition { Dx = x, Dy = y };
                else _rightStick = new JoystickPosition { Dx = x, Dy = y };
                PushInput();
            }

            private void PushInput()
            {
                _device?.Hid.Npads.Update(new System.Collections.Generic.List<GamepadInput>
                {
                    new GamepadInput
                    {
                        PlayerId = PlayerIndex.Handheld,
                        Buttons = _buttons,
                        LStick = _leftStick,
                        RStick = _rightStick
                    }
                });
            }

            public void ApplySettings(float scale, bool vsync, bool docked)
            {
                GraphicsConfig.ResScale = scale;
                _device.EnableDeviceVsync = vsync;
                _renderer.Window.ChangeVSyncMode(vsync);
                _device.System.ChangeDockedModeState(docked);
            }

            public void InstallFirmware(string path) => _content.InstallFirmware(path);

            public bool LoadGame(string path)
            {
                _vfs.ReloadKeySet();
                string ext = Path.GetExtension(path).ToLowerInvariant();
                bool hasProdKeys = File.Exists(Path.Combine(AppDataManager.KeysDirPath, "prod.keys"));
                WriteDiagnostic($"Load request: {Path.GetFileName(path)} | format={ext} | bytes={new FileInfo(path).Length} | prod.keys={hasProdKeys}");

                bool loaded = ext == ".xci" ? _device.LoadXci(path) :
                              ext == ".nsp" || ext == ".pfs0" ? _device.LoadNsp(path) :
                              ext == ".nca" ? _device.LoadNca(path) :
                              ext == ".nro" ? _device.LoadProgram(path) : false;

                WriteDiagnostic($"Load result: {loaded}");
                return loaded;
            }

            private void WriteDiagnostic(string message)
            {
                try
                {
                    string logPath = Path.Combine(AppDataManager.BaseDirPath, "android-emulation.log");
                    File.AppendAllText(logPath, DateTime.UtcNow.ToString("O") + " " + message + System.Environment.NewLine);
                }
                catch { }
            }

            public void Resize(int width, int height)
            {
                if (width > 0 && height > 0) _renderer.Window.SetSize(width, height);
            }

            public void StartRenderLoop()
            {
                if (_renderCancellation != null) return;
                var cancellation = new CancellationTokenSource();
                _renderCancellation = cancellation;
                _renderTask = Task.Run(() =>
                {
                    try
                    {
                        ((IRenderer)_renderer).RunLoop(() =>
                        {
                            _device.Gpu.SetGpuThread();
                            _device.Gpu.InitializeShaderCache(cancellation.Token);
                            while (!cancellation.IsCancellationRequested)
                            {
                                if (_device.WaitFifo()) _device.ProcessFrame();
                                while (_device.ConsumeFrameAvailable())
                                    _device.PresentFrame(() => { });
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        _activity.RunOnUiThread(() => _activity._status.Text = "Falha na renderização: " + ex.Message);
                        try
                        {
                            string log = Path.Combine(_activity.FilesDir.AbsolutePath, "Ryujinx", "android-crash.log");
                            File.AppendAllText(log, DateTime.UtcNow.ToString("O") + System.Environment.NewLine + ex + System.Environment.NewLine + System.Environment.NewLine);
                        }
                        catch { }
                    }
                });
            }

            public void Stop()
            {
                // SurfaceDestroyed and Activity.OnDestroy can race with host shutdown.
                // The Vulkan renderer and ANativeWindow must be released exactly once.
                if (Interlocked.Exchange(ref _stopStarted, 1) != 0)
                {
                    return;
                }

                CancellationTokenSource cancellation = _renderCancellation;
                try
                {
                    cancellation?.Cancel();
                    try { _renderTask?.Wait(); } catch { }
                    _renderTask = null;
                    _renderCancellation = null;
                    cancellation?.Dispose();

                    try
                    {
                        _device?.DisposeGpu();
                    }
                    finally
                    {
                        _device?.Dispose();
                        _device = null;
                    }
                }
                finally
                {
                    // Even if HLE/Vulkan disposal throws, do not leak the native
                    // reference retained by this AndroidHost instance.
                    NativeReleaseWindow(_nativeWindow);
                }
            }
        }
    }
}
