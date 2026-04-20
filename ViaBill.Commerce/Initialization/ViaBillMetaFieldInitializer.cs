using System;
using EPiServer.Framework;
using EPiServer.Framework.Initialization;
using EPiServer.Logging;
using Mediachase.MetaDataPlus;
using Mediachase.MetaDataPlus.Configurator;
using ViaBill.Commerce.Constants;

namespace ViaBill.Commerce.Initialization
{
    [InitializableModule]
    [ModuleDependency(typeof(EPiServer.Commerce.Initialization.InitializationModule))]
    public class ViaBillMetaFieldInitializer : IInitializableModule
    {
        private static readonly ILogger Logger =
            LogManager.GetLogger(typeof(ViaBillMetaFieldInitializer));
        
        private const string TargetMetaClassName = "OtherPayment"; // instead of: ViaBillPayment

        public void Initialize(InitializationEngine context)
        {            
            try
            {
                EnsureViaBillMetaFields();
            }
            catch (Exception ex)
            {
                Logger.Error("[ViaBill] Failed to register MetaClass fields.", ex);
                
                if (ex.InnerException != null)
                    Logger.Error(
                        $"[ViaBill] INNER: {ex.InnerException.GetType().FullName}: {ex.InnerException.Message}");
            }
        }

        public void Uninitialize(InitializationEngine context)
        {
            // Intentionally empty.
            // MetaClass and MetaFields are permanent schema — removal must be
            // performed via a dedicated uninstall script, never on app shutdown.
        }

        private static void EnsureViaBillMetaFields()
        {
            var ctx = MetaDataContext.Instance;

            // OtherPayment is the concrete MetaClass Commerce uses for this payment.
            // OrderFormPayment is a system class with read-only fields — cannot add to it.
            var metaClass = MetaClass.Load(ctx, "OtherPayment");
            if (metaClass == null)
            {
                Logger.Error("[ViaBill] MetaClass 'OtherPayment' not found — aborting.");                
                return;
            }            

            EnsureField(ctx, metaClass, ViaBillConstants.AuthorizedKey, MetaDataType.Boolean, 1);
            EnsureField(ctx, metaClass, ViaBillConstants.CapturedKey, MetaDataType.Boolean, 1);
            EnsureField(ctx, metaClass, ViaBillConstants.RefundedKey, MetaDataType.Boolean, 1);
            EnsureField(ctx, metaClass, ViaBillConstants.CapturedAmountKey, MetaDataType.Decimal, 0);
            EnsureField(ctx, metaClass, ViaBillConstants.RefundedAmountKey, MetaDataType.Decimal, 0);
            EnsureField(ctx, metaClass, ViaBillConstants.TransactionIdKey, MetaDataType.LongString, 4000);

            Logger.Information("[ViaBill] MetaClass field registration complete.");            
        }

        private static void EnsureField(
            MetaDataContext ctx,
            MetaClass metaClass,
            string fieldName,
            MetaDataType type,
            int length)
        {            
            var field = MetaField.Load(ctx, fieldName);
            if (field == null)
            {
                field = MetaField.Create(
                    ctx,
                    TargetMetaClassName,   // namespace scoped to this MetaClass
                    fieldName,
                    fieldName,              // friendly name
                    string.Empty,           // description
                    type,
                    length,
                    true,                   // allow nulls
                    false,                  // multi-language
                    false,                  // allow search
                    false);                 // encrypted

                Logger.Information($"[ViaBill] Created MetaField '{fieldName}' ({type}).");                
            }
            else
            {
                Logger.Information($"[ViaBill] Field already exists: {fieldName}");
            }

            // Link to MetaClass if not already attached (idempotent).
            if (metaClass.MetaFields[fieldName] == null)
            {
                metaClass.AddField(field);
                Logger.Information($"[ViaBill] Linked MetaField '{fieldName}' to '{metaClass.Name}'.");
            }
        }
    }
}