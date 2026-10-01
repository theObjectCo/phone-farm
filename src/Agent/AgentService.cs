using System;
using System.IO;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Runtime;
using PhoneFarm.Agent;

namespace PhoneFarm.Agent
{
    [Service(Exported = true)]
    [Register("pl/mz1/phonefarm/agent/AgentService")]
    public class AgentService : Service
    {
        public const string ActionStart = "pl.mz1.phonefarm.agent.START";
        public const string ActionStop = "pl.mz1.phonefarm.agent.STOP";

        AgentRuntime runtime;

        public override IBinder OnBind(Intent intent) => null;

        public override StartCommandResult OnStartCommand(Intent intent, StartCommandFlags flags, int startId)
        {
            if (intent?.Action == ActionStop)
            {
                runtime?.Dispose();
                runtime = null;
                StopSelf();
                return StartCommandResult.NotSticky;
            }

            Android.Util.Log.Info("phonefarm", "OnStartCommand action=" + intent?.Action);
            if (intent?.GetStringExtra("enable") == "1") AgentRuntime.SetConfigured(this, true);
            StartAsForeground();
            runtime ??= new AgentRuntime(this, FilesDir.Path, CacheDir.Path);
            return StartCommandResult.Sticky;
        }

        void StartAsForeground()
        {
            const string channelId = "phonefarm";
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            {
                var channel = new NotificationChannel(channelId, "PhoneFarm", NotificationImportance.Min);
                var manager = (NotificationManager)GetSystemService(NotificationService);
                manager.CreateNotificationChannel(channel);
            }
            var builder = new NotificationChannelNotificationBuilder(this, channelId);
            var notification = builder
                .SetContentTitle("PhoneFarm agent")
                .SetContentText("Working")
                .SetOngoing(true)
                .BuildNotification();

            if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
            {
                StartForeground(1, notification, (Android.Content.PM.ForegroundService)1073741824);
            }
            else
            {
                StartForeground(1, notification);
            }
        }

        class NotificationChannelNotificationBuilder
        {
            readonly Context context;
            readonly string channelId;
            string title = "PhoneFarm";
            string text = "";
            bool ongoing;

            public NotificationChannelNotificationBuilder(Context context, string channelId)
            {
                this.context = context;
                this.channelId = channelId;
            }

            public NotificationChannelNotificationBuilder SetContentTitle(string value) { title = value; return this; }
            public NotificationChannelNotificationBuilder SetContentText(string value) { text = value; return this; }
            public NotificationChannelNotificationBuilder SetOngoing(bool value) { ongoing = value; return this; }

            public global::Android.App.Notification BuildNotification()
            {
                var builder = Build.VERSION.SdkInt >= BuildVersionCodes.O
                    ? new Notification.Builder(context, channelId)
                    : new Notification.Builder(context);
                builder.SetContentTitle(title)
                    .SetContentText(text)
                    .SetSmallIcon(Android.Graphics.Drawables.Icon.CreateWithResource(context, (int)_Microsoft.Android.Resource.Designer.ResourceConstant.Drawable.ic_stat_phonefarm))
                    .SetOngoing(ongoing);
                return builder.Build();
            }
        }

        protected override void Dispose(bool disposing)
        {
            runtime?.Dispose();
            runtime = null;
            base.Dispose(disposing);
        }
    }
}
