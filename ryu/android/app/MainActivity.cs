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
    [Activity(Label = "Ryujinx Android", MainLauncher = true, Exported = true,
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
        private ProgressBar _importProgress;
        private volatile bool _isImporting;
        private volatile AndroidHost _host;
        private string _pendingAction;
        private volatile bool _surfaceReady;
        private int _surfaceGeneration;
        private int _surfaceWidth;
        private int _surfaceHeight;

        protected override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            Window.AddFlags(WindowManagerFlags.KeepScreenOn);

            var root = new FrameLayout(this);

            _surfaceView = new SurfaceView(this);
            _surfaceView.Holder.AddCallback(this);
            root.AddView(_surfaceView, new FrameLayout.LayoutParams(-1, -1));

            var topPanel = new LinearLayout(this) { Orientation = Orientation.Vertical };
            topPanel.SetBackgroundColor(global::Android.Graphics.Color.Argb(175, 20, 22, 26));
            var controls = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            var gameButton = new Button(this) { Text = "ABRIR JOGO" };
            var keysButton = new Button(this) { Text = "PROD.KEYS" };
            var firmwareButton = new Button(this) { Text = "FIRMWARE" };
            foreach (var button in new[] { gameButton, keysButton, firmwareButton })
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
            controls.AddView(gameButton, new LinearLayout.LayoutParams(0, 42, 1));
            controls.AddView(keysButton, new LinearLayout.LayoutParams(0, 42, 1));
            controls.AddView(firmwareButton, new LinearLayout.LayoutParams(0, 42, 1));
            topPanel.AddView(controls);
            topPanel.AddView(_status, new LinearLayout.LayoutParams(-1, 24));
            _importProgress = new ProgressBar(this, null, global::Android.Resource.Attribute.ProgressBarStyleHorizontal)
            {
                Max = 100,
                Progress = 0,
                Visibility = global::Android.Views.ViewStates.Gone
            };
            topPanel.AddView(_importProgress, new LinearLayout.LayoutParams(-1, 5));
            root.AddView(topPanel, new FrameLayout.LayoutParams(-1, -2, GravityFlags.Top));

            var bottomPanel = new LinearLayout(this) { Orientation = Orientation.Vertical };
            bottomPanel.SetBackgroundColor(global::Android.Graphics.Color.Argb(150, 20, 22, 26));
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
            bottomPanel.AddView(pad);

            var sticks = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            AddStickControl(sticks, "ANALÓGICO ESQUERDO", true);
            AddStickControl(sticks, "ANALÓGICO DIREITO", false);
            bottomPanel.AddView(sticks, new LinearLayout.LayoutParams(-1, 100));
            root.AddView(bottomPanel, new FrameLayout.LayoutParams(-1, -2, GravityFlags.Bottom));

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
            try
            {
                _surfaceReady = true;
                int generation = Interlocked.Increment(ref _surfaceGeneration);
                long nativeWindow = NativeFromSurface(JNIEnv.Handle, IntPtr.Zero, holder.Surface.Handle);
                if (nativeWindow == 0) throw new InvalidOperationException("ANativeWindow não foi criado.");
                _status.Text = "Inicializando Vulkan...";
                _ = InitializeHostAsync(nativeWindow, generation);
            }
            catch (Exception ex) { _status.Text = "Erro Vulkan: " + ex.Message; }
        }

        private async Task InitializeHostAsync(long nativeWindow, int generation)
        {
            AndroidHost host = null;
            try
            {
                host = await Task.Run(() => new AndroidHost(this, nativeWindow));
                if (!_surfaceReady || generation != Volatile.Read(ref _surfaceGeneration))
                {
                    await Task.Run(host.Stop);
                    return;
                }

                _host = host;
                host.Resize(_surfaceWidth, _surfaceHeight);
                _status.Text = "Vulkan inicializado. Importe prod.keys e selecione um jogo.";

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
                if (host != null) await Task.Run(host.Stop);
                else NativeReleaseWindow(nativeWindow);

                if (_surfaceReady && generation == Volatile.Read(ref _surfaceGeneration))
                    _status.Text = "Erro Vulkan: " + ex.Message;
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
            Interlocked.Increment(ref _surfaceGeneration);
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
                if (loaded) host.StartRenderLoop();
            }
            catch (Exception ex)
            {
                if (_host == host) _status.Text = "Falha ao carregar: " + ex.Message;
            }
        }

        protected override void OnDestroy()
        {
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
                    GraphicsConfig.EnableShaderCache = true;

                    _vfs = VirtualFileSystem.CreateInstance();
                    _libHac = new LibHacHorizonManager();
                    _libHac.InitializeFsServer(_vfs);
                    _libHac.InitializeArpServer();
                    _libHac.InitializeBcatServer();
                    _libHac.InitializeSystemClients();
                    _content = new ContentManager(_vfs);
                    var accounts = new AccountManager(_libHac.RyujinxClient);
                    var persistence = new UserChannelPersistence();

                    renderer = new VulkanRenderer(Vk.GetApi(), new IntPtr(nativeWindow), "");
                    var configuration = new HLEConfiguration(
                        _vfs, _libHac, _content, accounts, persistence, renderer,
                        new AndroidAudioHardwareDeviceDriver(),
                        MemoryConfiguration.MemoryConfiguration4GiB,
                        new AndroidHostUIHandler(activity),
                        SystemLanguage.BrazilianPortuguese,
                        RegionCode.USA,
                        true, false, true, false,
                        IntegrityCheckLevel.None, 0, 0, "UTC",
                        MemoryManagerMode.HostMapped,
                        true, AspectRatio.Fixed16x9, 1.0f, false, "",
                        Ryujinx.Common.Configuration.Multiplayer.MultiplayerMode.Disabled);
                    device = new RyujinxSwitch(configuration);
                    renderer.Initialize(GraphicsDebugLevel.None);

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
                CancellationTokenSource cancellation = _renderCancellation;
                cancellation?.Cancel();
                try { _renderTask?.Wait(); } catch { }
                _renderTask = null;
                _renderCancellation = null;
                cancellation?.Dispose();
                _device?.DisposeGpu();
                _device?.Dispose();
                NativeReleaseWindow(_nativeWindow);
            }
        }
    }
}
