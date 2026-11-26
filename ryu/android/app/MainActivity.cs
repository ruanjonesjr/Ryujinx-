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
        private Button _engineToggleButton;
        private ProgressBar _importProgress;
        private LinearLayout _bottomPanel;
        private LinearLayout _topPanel;
        private volatile bool _isImporting;
        private volatile AndroidHost _host;
        private string _pendingAction;
        private volatile bool _surfaceReady;
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

            _surfaceView = new SurfaceView(this);
            _surfaceView.Holder.AddCallback(this);
            root.AddView(_surfaceView, new FrameLayout.LayoutParams(-1, -1));

            _topPanel = new LinearLayout(this) { Orientation = Orientation.Vertical };
            _topPanel.SetBackgroundColor(global::Android.Graphics.Color.Rgb(25, 29, 36));
            _topPanel.SetPadding(20, 18, 20, 18);
            var controls = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            var gameButton = new Button(this) { Text = "ABRIR JOGO" };
            var keysButton = new Button(this) { Text = "PROD.KEYS" };
            var firmwareButton = new Button(this) { Text = "FIRMWARE" };
            var settingsButton = new Button(this) { Text = "CONFIGURAÇÕES" };
            _engineToggleButton = new Button(this) { Text = "DESLIGAR MOTOR" };
            foreach (var button in new[] { gameButton, keysButton, firmwareButton, settingsButton, _engineToggleButton })
            {
                button.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 10f);
                button.SetMinimumHeight(38);
                button.SetPadding(2, 0, 2, 0);
            }

            _status = new TextView(this)
            {
                Text = "Inicializando Ryujinx Android...",
                Gravity = GravityFlags.CenterVertical,
                TextSize = 11
            };
            _status.SetTextColor(global::Android.Graphics.Color.White);
            gameButton.Click += (_, _) => PickFile(PickGameRequest, "*/*", "game");
            keysButton.Click += (_, _) => PickFile(PickKeysRequest, "*/*", "keys");
            firmwareButton.Click += (_, _) => PickFile(PickFirmwareRequest, "application/zip", "firmware");
            settingsButton.Click += (_, _) => ShowPerformanceSettings();
            _engineToggleButton.Click += (_, _) => ToggleVulkanHost();
            controls.AddView(gameButton, new LinearLayout.LayoutParams(0, 42, 1));
            controls.AddView(keysButton, new LinearLayout.LayoutParams(0, 42, 1));
            controls.AddView(firmwareButton, new LinearLayout.LayoutParams(0, 42, 1));
            controls.AddView(settingsButton, new LinearLayout.LayoutParams(0, 42, 1));
            controls.AddView(_engineToggleButton, new LinearLayout.LayoutParams(0, 42, 1));
            var brand = new TextView(this) { Text = "RYUJINX", TextSize = 26, Gravity = GravityFlags.Center };
            brand.SetTextColor(global::Android.Graphics.Color.White);
            brand.SetTypeface(null, global::Android.Graphics.TypefaceStyle.Bold);
            var subtitle = new TextView(this) { Text = "EMULADOR DE CONSOLE • ANDROID", TextSize = 10, Gravity = GravityFlags.Center };
            subtitle.SetTextColor(global::Android.Graphics.Color.Rgb(170, 180, 195));
            _topPanel.AddView(brand, new LinearLayout.LayoutParams(-1, 42));
            _topPanel.AddView(subtitle, new LinearLayout.LayoutParams(-1, 24));
            var menuSpacer = new View(this);
            _topPanel.AddView(menuSpacer, new LinearLayout.LayoutParams(1, 12));
            _topPanel.AddView(controls);
            _topPanel.AddView(_status, new LinearLayout.LayoutParams(-1, 34));
            _importProgress = new ProgressBar(this, null, global::Android.Resource.Attribute.ProgressBarStyleHorizontal)
            {
                Max = 100,
                Progress = 0,
                Visibility = global::Android.Views.ViewStates.Gone
            };
            _topPanel.AddView(_importProgress, new LinearLayout.LayoutParams(-1, 5));
            var topParams = new FrameLayout.LayoutParams(-1, -2, GravityFlags.Center);
            topParams.SetMargins(20, 20, 20, 20);
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
                _status.Text = "Aguardando superfície Vulkan...";
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

        private void ShowPerformanceSettings()
        {
            float[] scales = { 0.5f, 0.75f, 1.0f, 1.25f };
            string[] labels = { "0,5x - desempenho", "0,75x - equilibrado", "1,0x - padrão", "1,25x - qualidade" };
            var preferences = GetSharedPreferences("ryujinx_android", FileCreationMode.Private);
            float current = preferences.GetFloat("resolution_scale", 0.75f);
            int selected = Array.IndexOf(scales, current);
            if (selected < 0) selected = 1;

            var content = new LinearLayout(this) { Orientation = Orientation.Vertical };
            content.SetPadding(32, 12, 32, 0);

            var scaleLabel = new TextView(this) { Text = "Resolução interna", TextSize = 14 };
            content.AddView(scaleLabel);
            var scaleSpinner = new Spinner(this);
            var adapter = new ArrayAdapter<string>(this, global::Android.Resource.Layout.SimpleSpinnerItem, labels);
            adapter.SetDropDownViewResource(global::Android.Resource.Layout.SimpleSpinnerDropDownItem);
            scaleSpinner.Adapter = adapter;
            scaleSpinner.SetSelection(selected);
            content.AddView(scaleSpinner);

            var vsyncCheck = new CheckBox(this)
            {
                Text = "VSync (reduz cortes na imagem)",
                Checked = preferences.GetBoolean("vsync", true)
            };
            content.AddView(vsyncCheck);

            var dockedCheck = new CheckBox(this)
            {
                Text = "Modo Dock (maior carga no aparelho)",
                Checked = preferences.GetBoolean("docked_mode", false)
            };
            content.AddView(dockedCheck);

            new AlertDialog.Builder(this)
                .SetTitle("Configurações de desempenho")
                .SetView(content)
                .SetNegativeButton("Cancelar", (_, _) => { })
                .SetPositiveButton("Aplicar", (_, _) =>
                {
                    float scale = scales[Math.Max(0, scaleSpinner.SelectedItemPosition)];
                    bool vsync = vsyncCheck.Checked;
                    bool docked = dockedCheck.Checked;
                    preferences.Edit()
                        .PutFloat("resolution_scale", scale)
                        .PutBoolean("vsync", vsync)
                        .PutBoolean("docked_mode", docked)
                        .Apply();

                    GraphicsConfig.ResScale = scale;
                    _host?.ApplySettings(scale, vsync, docked);
                    _status.Text = $"Configuração aplicada: {scale:0.00}x | {(docked ? "Dock" : "Handheld")}.";
                })
                .Show();
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
                    ? Guid.NewGuid().ToString("N") + extension
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

        public void SurfaceCreated(ISurfaceHolder holder)
        {
            // Defer JNI/ANativeWindow access until the Surface callback has returned.
            _surfaceHolder = holder;
            _surfaceReady = true;
            Interlocked.Increment(ref _surfaceGeneration);
            _status.Text = "Tela pronta. Iniciando o motor Vulkan automaticamente...";
            _ = Task.Delay(300).ContinueWith(_ => RunOnUiThread(StartVulkanHost));
        }

        private void ToggleVulkanHost()
        {
            AndroidHost host = _host;
            if (host == null)
            {
                StartVulkanHost();
                return;
            }

            if (Interlocked.CompareExchange(ref _hostInitializationInProgress, 1, 0) != 0)
            {
                _status.Text = "Aguarde a operação atual do motor terminar.";
                return;
            }

            _host = null;
            _status.Text = "Desligando o motor Vulkan...";
            _engineToggleButton.Text = "AGUARDE...";
            _ = Task.Run(() =>
            {
                try { host.Stop(); }
                finally
                {
                    Interlocked.Exchange(ref _hostInitializationInProgress, 0);
                    RunOnUiThread(() =>
                    {
                        if (!_surfaceReady) return;
                        _engineToggleButton.Text = "ATIVAR MOTOR";
                        _status.Text = "Motor Vulkan desligado.";
                    });
                }
            });
        }

        private void StartVulkanHost()
        {
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
                nativeWindow = NativeFromSurface(JNIEnv.Handle, IntPtr.Zero, holder.Surface.Handle);
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
                host = await Task.Run(() => new AndroidHost(this, nativeWindow));
                if (!_surfaceReady || generation != Volatile.Read(ref _surfaceGeneration))
                {
                    Interlocked.CompareExchange(ref _pendingNativeWindow, 0, nativeWindow);
                    await Task.Run(host.Stop);
                    return;
                }

                _host = host;
                Interlocked.CompareExchange(ref _pendingNativeWindow, 0, nativeWindow);
                host.Resize(_surfaceWidth, _surfaceHeight);
                _status.Text = "Motor Vulkan ativo. Importe prod.keys e selecione um jogo.";
                RunOnUiThread(() => { if (_engineToggleButton != null) _engineToggleButton.Text = "DESLIGAR MOTOR"; });

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

            _status.Text = "Falha ao iniciar Vulkan. Toque para ver detalhes.";
            if (_engineToggleButton != null) _engineToggleButton.Text = "TENTAR NOVAMENTE";
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

        [DllImport("shaderc_shared", EntryPoint = "shaderc_compiler_initialize", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr ShadercCompilerInitialize();

        [DllImport("shaderc_shared", EntryPoint = "shaderc_compiler_release", CallingConvention = CallingConvention.Cdecl)]
        private static extern void ShadercCompilerRelease(IntPtr compiler);

        private static void VerifyShaderCompiler()
        {
            IntPtr compiler = ShadercCompilerInitialize();
            if (compiler == IntPtr.Zero)
                throw new InvalidOperationException("O compilador Vulkan shaderc não conseguiu inicializar.");
            ShadercCompilerRelease(compiler);
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

                    VerifyShaderCompiler();
                    renderer = new VulkanRenderer(Vk.GetApi(), new IntPtr(nativeWindow), "");
                    // Initialize the Vulkan renderer before constructing HLE.
                    // Ryujinx's GPU subsystem may query renderer capabilities during
                    // Switch construction, so initializing it afterwards can abort
                    // engine startup on Android.
                    renderer.Initialize(GraphicsDebugLevel.None);

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
