# ViaBill Payments Module for Optimizely Commerce 14

The ViaBill Payments module provides a seamless integration between Optimizely Commerce 14 (CMS 12) and the ViaBill payment gateway. It enables secure payment processing, post-checkout order management (capture, refund, void), and dynamic ViaBill Price Tag displays throughout your storefront.

This repository contains two main components:
1. **ViaBill.Commerce**: The core class library module containing the payment gateway, API services, models, and administrative controllers.
2. **foundation-patches**: A set of source file patches designed to integrate the module into the Optimizely Commerce Foundation reference storefront.

---

## Installation

To install the ViaBill Payments module into your Optimizely Commerce solution, follow these steps:

### 1. Add the Core Module
Add the `ViaBill.Commerce` project to your solution and reference it from your main web project (e.g., your Foundation storefront).

### 2. Apply Foundation Patches
If you are using the Optimizely Foundation reference architecture, you must apply the provided storefront patches. Copy the contents of the `foundation-patches/src/Foundation` directory into your project's `src/Foundation` directory. 

For detailed instructions on the patched files and dependency injection registration, please refer to the [Foundation Patches README](foundation-patches/README.md).

### 3. Register the Payment Method
1. Log in to the Optimizely Commerce Manager.
2. Navigate to **Administration > Order System > Payments > English** (or your target language).
3. Click **New** to create a payment method.
4. Set the **System Keyword** to exactly `ViaBill` (case-sensitive).
5. Set the **Class Name** to `ViaBill.Commerce.Gateway.ViaBillPaymentGateway, ViaBill.Commerce`.
6. Save the payment method, then open its **Parameters** tab to configure the settings.

---

## Configuration Parameters

The module is configured via the **Parameters** tab of the ViaBill payment method in Commerce Admin. The following parameters must be added and configured:

| Parameter Name | Description |
|---|---|
| `ApiKey` | Your ViaBill API Key. Retrieved by the ViaBill Account Login or Registration tasks. Use the endpoints `https://your-domain.com/viabill-login/` or `https://your-domain.com/viabill-register/` to retrieve the ApiKey, along with the value for the `Secret` and `PriceTagScript` parameters. |
| `Secret` | Your ViaBill API Secret, used for signature generation. Retrieved by the ViaBill Account Login or Registration tasks. |
| `PriceTagScript` | The script ID for your ViaBill Price Tag. Retrieved by the ViaBill Account Login or Registration tasks. |
| `SuccessUrl` | The absolute URL ViaBill redirects the browser to after a successful payment. Should be `https://your-domain.com/viabill/success`. |
| `CancelUrl` | The absolute URL ViaBill redirects the browser to when the customer cancels. Should be `https://your-domain.com/viabill/cancel`. |
| `CallbackUrl` | The server-to-server webhook URL ViaBill posts payment status updates to. Should be `https://your-domain.com/viabill/callback`. |
| `TestMode` | Set to `true` for test orders, or `false` for actual orders. After testing is complete, this should be set to `false`. Note that `TestMode` is independent of the base URL. |
| `AutoCapture` | If set to `Yes`, after the payment authorization the module will automatically issue a capture request. If set to `No`, the capture request must be issued manually via the ViaBill Payments tab of the order. |
| `OrderConfirmationUrl` | This is where the user will be redirected after a successful payment. If left empty, the module will try to retrieve the default "thank you for your order" page. |
| `CheckoutUrl` | This is where the user will be redirected after a cancelled payment. If left empty, the module will try to retrieve the default checkout page. |
| `DebugMode` | If set to `true`, the module will keep track of certain actions in the application's log file. Once testing is completed, set it to `false`. |

---

## Development and Testing

For testing the authorization checkout requests and the subsequent capture, refunds, and void requests, you need a ViaBill account. 

During the development and testing of the integration, it is recommended that the following constant in `ViaBillConstants.cs` is set to `true`:

```csharp
public const bool DevelopmentMode = true;
```

Setting this to `true` will result in the module using the development base URL `https://secure-test.viabill.com` instead of the production URL `https://secure.viabill.com`. 

Developers can retrieve new user account credentials using the built-in registration helper:
`https://your-domain.com/viabill-register`

Or, for existing ViaBill account users, the login helper:
`https://your-domain.com/viabill-login`

Likewise, once the installation has been verified and is working as expected, `DevelopmentMode` should be set to `false` so that transactions are routed to the production/live site.

> **Important:** If the merchant wants to receive actual payments, the `TestMode` configuration parameter in Commerce Admin must also be set to `false`.

---

## ViaBill PriceTags

ViaBill PriceTags are dynamic UI widgets that display instalment information and pricing options to customers directly on product pages, cart views, and during checkout. They help increase conversion rates by showing customers how they can split their payments.

The module includes a `ViaBillPriceTag` view component that automatically injects the necessary HTML attributes (`data-view`, `data-price`, `data-currency`) and asynchronously loads the ViaBill script using your configured `PriceTagScript` ID.

For more detailed information on configuring and styling PriceTags, please refer to the official ViaBill PriceTag documentation:
[https://viabill.io/api/pricetag/](https://viabill.io/api/pricetag/)

---

## Order Management

Authorized administrators can manage ViaBill payments directly from the storefront or the Optimizely Customer Service UI.

- **Storefront Admin UI**: Accessible at `/viabill/admin`, this interface allows administrators to capture, refund, and void payments. A link to this interface is automatically added to the Order Details page in the "My Account" section for orders paid with ViaBill.
- **Customer Service UI**: The module automatically registers a "ViaBill Payments" tab in the Optimizely Commerce Customer Service interface for purchase orders, providing seamless back-office management.
