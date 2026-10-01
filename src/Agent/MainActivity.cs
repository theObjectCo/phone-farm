using System;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Runtime;
using Android.Widget;

namespace PhoneFarm.Agent
{
    [Activity(
        MainLauncher = true,
        Label = "PhoneFarm Agent",
        Exported = true)]
    public class MainActivity : Activity
    {
        protected override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            var layout = new LinearLayout(this) { Orientation = Orientation.Vertical };
            layout.SetPadding(48, 96, 48, 48);

            var status = new TextView(this) { TextSize = 16f };
            status.Text = AgentRuntime.DescribeState(this);
            layout.AddView(status);

            var start = new Button(this) { Text = "Start agent" };
            start.Click += (s, e) =>
            {
                AgentRuntime.SetConfigured(this, true);
                var intent = new Intent(this, typeof(AgentService));
                if (Build.VERSION.SdkInt >= BuildVersionCodes.O) StartForegroundService(intent);
                else StartService(intent);
                status.Text = AgentRuntime.DescribeState(this);
            };
            layout.AddView(start);

            SetContentView(layout);
        }
    }
}
