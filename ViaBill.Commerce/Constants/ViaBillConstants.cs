namespace ViaBill.Commerce.Constants
{
    public static class ViaBillConstants
    {
        // System keyword — must match exactly what you enter in Commerce Admin
        public const string SystemKeyword = "ViaBill";

        // ViaBill REST API base URLs
        public const string ProductionApiBaseUrl  = "https://secure.viabill.com";
        public const string TestApiBaseUrl        = "https://secure.viabill.com"; // ViaBill uses same base; test mode is toggled via credentials
        public const string DevelopmentApiBaseUrl = "https://secure-test.viabill.com"; // ViaBill uses same base; test mode is toggled via credentials
        public const bool DevelopmentMode         = true;
        public const string AddonName             = "drupal"; // eventually: "optimizely"

        // API endpoint paths        
        public const string CheckoutPath = "/api/checkout-authorize/addon/";
        public const string CapturePath  = "/api/transaction/capture";
        public const string RefundPath   = "/api/transaction/refund";
        public const string CancelPath   = "/api/transaction/cancel";        

        // Admin UI Settings dictionary keys (populated from the second tab in Commerce Admin)
        public const string ApiKeySettingKey      = "ApiKey";
        public const string SecretSettingKey      = "Secret";
        public const string PriceTagScript        = "PriceTagScript";
        public const string TestModeSettingKey    = "TestMode";       // "true" / "false"
        public const string DebugModeSettingKey   = "DebugMode";      // "true" / "false"

        public const string SuccessUrlKey = "SuccessUrl";
        public const string CancelUrlKey = "CancelUrl";
        public const string CallbackUrlKey = "CallbackUrl";
        public const string OrderConfirmationUrlKey = "OrderConfirmationUrl";
        public const string CheckoutUrlKey = "CheckoutUrl";

        // Payment property keys stored on IPayment.Properties
        public const string TransactionIdKey  = "ViaBillTransactionId";
        public const string RedirectUrlKey    = "ViaBillRedirectUrl";
        public const string CallbackStatusKey = "ViaBillCallbackStatus";
        
        public const string AutoCaptureSettingKey = "AutoCapture";

        public const string AuthorizedKey = "ViaBillAuthorized"; // "true" once ViaBill approves the authorization
        public const string CapturedKey = "ViaBillCaptured";   // "true" once funds have been captured
        public const string RefundedKey = "ViaBillRefunded";   // "true" once a refund (full or partial) has been issued        

        public const string CapturedAmountKey = "ViaBillCapturedAmount"; // running total of all captures
        public const string RefundedAmountKey = "ViaBillRefundedAmount"; // running total of all refunds

        // ViaBill transaction status values returned in callbacks        
        public const string StatusApproved = "APPROVED";
        public const string StatusCancelled = "CANCELLED";
        public const string StatusPending = "PENDING";
        public const string StatusRejected = "REJECTED";
    }
}

