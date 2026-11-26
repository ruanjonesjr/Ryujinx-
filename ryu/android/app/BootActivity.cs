using global::Android.App;
using global::Android.OS;
using global::Android.Views;
using global::Android.Widget;

namespace Ryujinx.Android
{
    // .NET for Android generates the Java activity name. Do not hard-code the
    // managed namespace in AndroidManifest.xml.
    [Activity(Label = "Ryujinx Android", MainLauncher = true, Exported = true,
        ScreenOrientation = global::Android.Content.PM.ScreenOrientation.Portrait)]
    public sealed class BootActivity : Activity
    {
        protected override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            Window.AddFlags(WindowManagerFlags.KeepScreenOn);
            Window.SetStatusBarColor(global::Android.Graphics.Color.Rgb(18, 22, 29));
            Window.SetNavigationBarColor(global::Android.Graphics.Color.Rgb(18, 22, 29));

            var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
            root.SetGravity(GravityFlags.Center);
            root.SetPadding(32, 32, 32, 32);
            root.SetBackgroundColor(global::Android.Graphics.Color.Rgb(18, 22, 29));

            var title = new TextView(this) { Text = "RYUJINX", TextSize = 32, Gravity = GravityFlags.Center };
            title.SetTextColor(global::Android.Graphics.Color.White);
            title.SetTypeface(null, global::Android.Graphics.TypefaceStyle.Bold);

            var subtitle = new TextView(this) { Text = "Ryujinx Android iniciado", TextSize = 15, Gravity = GravityFlags.Center };
            subtitle.SetTextColor(global::Android.Graphics.Color.Rgb(190, 198, 210));

            var start = new Button(this) { Text = "ABRIR EMULADOR" };
            start.SetOnClickListener(new StartEmulatorClickListener(this));

            root.AddView(title, new LinearLayout.LayoutParams(-1, -2));
            root.AddView(subtitle, new LinearLayout.LayoutParams(-1, -2));
            root.AddView(new View(this), new LinearLayout.LayoutParams(1, 28));
            root.AddView(start, new LinearLayout.LayoutParams(-1, 56));
            SetContentView(root);
        }

        private sealed class StartEmulatorClickListener : Java.Lang.Object, View.IOnClickListener
        {
            private readonly BootActivity _activity;
            public StartEmulatorClickListener(BootActivity activity) { _activity = activity; }
            public void OnClick(View view)
            {
                var intent = new global::Android.Content.Intent();
                intent.SetClass(_activity, typeof(MainActivity));
                _activity.StartActivity(intent);
            }
        }
    }
}