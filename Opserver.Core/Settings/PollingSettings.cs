namespace StackExchange.Opserver
{
    public class PollingSettings : ModuleSettings
    {
        public override bool Enabled => Windows != null;

        public WindowsPollingSettings Windows { get; set; }
        public class WindowsPollingSettings
        {
            /// <summary>
            /// Maximum timeout in milliseconds before giving up on a poll
            /// </summary>
            public int QueryTimeoutMs { get; set; } = 30 * 1000;

            /// <summary>
            /// User to authenticate as, if not present then impersonation will be used
            /// </summary>
            public string AuthUser { get; set; }

            /// <summary>
            /// Password for user to authenticate as, if not present then impersonation will be used
            /// </summary>
            public string AuthPassword { get; set; }
        }
    }
}
