# ViaBill Payments — Foundation Patches

This directory contains the source file patches required to integrate the **ViaBill Payments module** into the [Optimizely Commerce Foundation](https://github.com/episerver/Foundation) storefront. The patches enable ViaBill payment processing, post-checkout order management, and dynamic ViaBill Price Tag displays throughout the storefront.

The module targets **Optimizely Commerce 14** (CMS 12, .NET 6) and is designed to work alongside the Foundation reference architecture.

---

## Prerequisites

Before applying the patches, ensure the following are in place:

- A working Foundation storefront project targeting Optimizely Commerce 14 (EPiServer.Commerce.Core 14.x).
- The `ViaBill.Commerce` class library project (included in this repository) is referenced by your Foundation project.
- A ViaBill merchant account with an **API Key**, **API Secret**, and **Price Tag Script ID**. These can be retrieved from the ViaBill merchant portal or via the built-in credential helpers described in the [Configuration](#configuration) section below.

---

## Directory Structure

The patches mirror the Foundation project's directory layout exactly. The following files are included:

| Patch File | Target Path in Foundation Project |
|---|---|
| `ViaBillCallbackController.cs` | `src/Foundation/Features/Checkout/` |
| `ViaBillOrderApiController.cs` | `src/Foundation/Features/Checkout/` |
| `ViaBillOrderController.cs` | `src/Foundation/Features/Checkout/` |
| `ViaBillSettingsResolver.cs` | `src/Foundation/Features/Checkout/` |
| `_ViaBillPaymentMethod.cshtml` | `src/Foundation/Features/Checkout/` |
| `Index.cshtml` | `src/Foundation/Features/MyAccount/OrderDetails/` |
| `_ItemTemplate.cshtml` | `src/Foundation/Features/NamedCarts/DefaultCart/` |
| `_ItemTemplate.cshtml` | `src/Foundation/` *(root-level cart template)* |
| `InitializeSite.cs` | `src/Foundation/Infrastructure/` |
| `Default.cshtml` | `src/Foundation/Views/Shared/Components/ViaBillPriceTag/` |
| `Index.cshtml` | `src/Foundation/Views/ViaBillOrder/` |

---

## Installation

### Step 1 — Copy the Patch Files

Copy the entire contents of the `src/Foundation` directory from this `foundation-patches` folder into the corresponding `src/Foundation` directory of your Foundation project.

> **Important:** The patches for `InitializeSite.cs`, `Index.cshtml` (Order Details), and `_ItemTemplate.cshtml` are **replacements** for existing Foundation files. Back up your originals before overwriting them, and review the diff carefully if you have made custom modifications to those files.

### Step 2 — Reference the ViaBill.Commerce Project

Add a project reference (or NuGet package reference) to `ViaBill.Commerce` in your Foundation `.csproj` file:

```xml
<ProjectReference Include="..\ViaBill.Commerce\ViaBill.Commerce.csproj" />
```

### Step 3 — Register ViaBill Services in the DI Container

The patched `InitializeSite.cs` already contains all required service registrations. Verify that the following lines are present in the `ConfigureContainer` method of `Foundation.Infrastructure.InitializeSite`:

```csharp
using ViaBill.Commerce.Services;

// Inside ConfigureContainer:
_services.AddTransient<IPaymentMethod, ViaBillPaymentOption>();
_services.AddSingleton<ViaBillSettingsResolver>();
_services.AddHttpClient();
_services.AddTransient<ViaBill.Commerce.Services.ViaBillApiService>();
_services.AddScoped<IViaBillSettingsService, ViaBillSettingsService>();
```

If you are not replacing `InitializeSite.cs` wholesale, merge these registrations into your existing `ConfigureContainer` method.

### Step 4 — Create the Payment Method in Commerce Admin

1. Log in to the Optimizely Commerce Manager.
2. Navigate to **Administration > Order System > Payments > English** (or your target language).
3. Click **New** to create a payment method, or select an existing one to edit.
4. Set the **System Keyword** to exactly `ViaBill` (case-sensitive).
5. Set the **Class Name** to `ViaBill.Commerce.Gateway.ViaBillPaymentGateway, ViaBill.Commerce`.
6. Save the payment method, then open its **Parameters** tab and configure the settings described in the [Configuration](#configuration) section below.

### Step 5 — Verify Meta Field Registration

The `ViaBillMetaFieldInitializer` (included in the `ViaBill.Commerce` module) automatically registers the required custom meta fields on the `OtherPayment` MetaClass when the application starts. No manual database migration is required. The following fields are created if they do not already exist:

| Field Name | Type | Purpose |
|---|---|---|
| `ViaBillAuthorized` | Boolean | Set to `true` once ViaBill approves the authorization |
| `ViaBillCaptured` | Boolean | Set to `true` once funds have been captured |
| `ViaBillRefunded` | Boolean | Set to `true` once a refund has been issued |
| `ViaBillCapturedAmount` | Decimal | Running total of all captured amounts |
| `ViaBillRefundedAmount` | Decimal | Running total of all refunded amounts |
| `ViaBillTransactionId` | LongString | The ViaBill transaction reference |

---

## Configuration

All ViaBill settings are stored as **Payment Method Parameters** in Commerce Admin and are read at runtime by `ViaBillSettingsResolver`. The following parameters must be configured:

| Parameter Key | Required | Description |
|---|---|---|
| `ApiKey` | Yes | Your ViaBill API Key |
| `Secret` | Yes | Your ViaBill API Secret, used for SHA-256 signature generation |
| `PriceTagScript` | Yes | The Price Tag script ID provided by ViaBill (determines whether the module is considered available) |
| `TestMode` | Yes | Set to `true` to use the ViaBill test environment; `false` for production |
| `DebugMode` | No | Set to `true` to write detailed request/response logs via EPiServer's logging framework |
| `AutoCapture` | No | Set to `true` or `yes` to automatically capture funds immediately upon payment approval. When disabled, payments are authorized only and must be captured manually from the order management UI |
| `SuccessUrl` | Yes | The absolute URL ViaBill redirects the browser to after a successful payment (e.g., `https://yourdomain.com/viabill/success`) |
| `CancelUrl` | Yes | The absolute URL ViaBill redirects the browser to when the customer cancels (e.g., `https://yourdomain.com/viabill/cancel`) |
| `CallbackUrl` | Yes | The server-to-server webhook URL ViaBill posts payment status updates to (e.g., `https://yourdomain.com/viabill/callback`) |
| `OrderConfirmationUrl` | No | Custom URL for the order confirmation page. Falls back to Foundation's `ReferencePageSettings.OrderConfirmationPage` if not set |
| `CheckoutUrl` | No | Custom URL for the checkout page. Falls back to Foundation's `ReferencePageSettings.CheckoutPage` if not set |

### Retrieving Credentials

If you do not yet have API credentials, the module provides two helper pages accessible to users in the `CommerceAdmins` or `WebAdmins` roles:

- **`/viabill-login`** — Log in with your existing ViaBill merchant account to retrieve your `ApiKey`, `Secret`, and `PriceTagScript`.
- **`/viabill-register`** — Register a new ViaBill merchant account and receive your credentials upon successful registration.

---

## Payment Flow

The following table summarises the end-to-end payment lifecycle managed by the Foundation patches:

| Stage | Route / Component | Description |
|---|---|---|
| Checkout | `_ViaBillPaymentMethod.cshtml` | Renders the ViaBill payment option and Price Tag at checkout |
| Authorization | `ViaBillPaymentGateway` | Initiates the ViaBill checkout session and returns a redirect URL |
| Redirect | Browser | Customer is redirected to ViaBill to approve the payment |
| Callback | `POST /viabill/callback` | ViaBill posts the payment status; the cart is converted to a purchase order |
| Success redirect | `GET /viabill/success` | Customer is redirected back to the order confirmation page |
| Cancel redirect | `GET /viabill/cancel` | Customer is redirected back to the checkout page |
| Auto-capture | `ViaBillCallbackController` | If `AutoCapture` is enabled, a capture request is made automatically upon approval |
| Manual capture | `POST /viabill/admin/capture` | Administrators can manually capture authorized payments (full or partial) |
| Refund | `POST /viabill/admin/refund` | Administrators can refund captured payments (full or partial) |
| Void | `POST /viabill/admin/void` | Administrators can void authorized, uncaptured payments |

---

## Order Management UI

Authorized users (`CommerceAdmins` or `WebAdmins`) can manage ViaBill payments from the dedicated admin interface at `/viabill/admin`. The interface allows looking up an order by order number and performing capture, refund, and void operations.

A link to this interface is also injected into the **My Account > Order Details** page for any order paid with ViaBill.

Additionally, the `ViaBill.Commerce` module registers a **"ViaBill Payments" tab** in the Optimizely Customer Service UI for purchase orders, served via the script at `/js/ViaBillAdminTab/ViaBillAdminTab.js`.

---

## ViaBill Price Tag

The ViaBill Price Tag is a widget that displays instalment information to customers. It is rendered using the `ViaBillPriceTag` view component, which is included in the `ViaBill.Commerce` module. The Foundation patches inject this component in the following locations:

| Location | View | `data-view` Value |
|---|---|---|
| Cart item (large cart) | `_ItemTemplate.cshtml` | `basket` |
| Checkout payment step | `_ViaBillPaymentMethod.cshtml` | `payment` |
| Shared component view | `Views/Shared/Components/ViaBillPriceTag/Default.cshtml` | Configurable |

The Price Tag script is loaded asynchronously from `https://pricetag.viabill.com/script/{PriceTagScript}` and is injected only once per page using a `ViewData` guard to prevent duplicate script tags.

> **Note:** The `ViaBillSettingsService` determines module availability based solely on whether the `PriceTagScript` parameter is non-empty. If this value is not configured, the Price Tag component will render no output and the module will be considered unavailable.