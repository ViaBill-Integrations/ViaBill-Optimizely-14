using EPiServer.Commerce.UI.CustomerService.Extensibility;
using EPiServer.Framework;
using EPiServer.Framework.Initialization;
using EPiServer.ServiceLocation;
using Microsoft.Extensions.DependencyInjection;  // ← ADD THIS

namespace ViaBill.Commerce.OrderManagement
{
    [InitializableModule]
    [ModuleDependency(typeof(EPiServer.Web.InitializationModule))]
    public class ViaBillCsrExtensionInitialization : IConfigurableModule
    {
        public void ConfigureContainer(ServiceConfigurationContext context)
        {
            context.Services.Configure<ExtendedComponentOptions>(options =>
            {
                options.ExtendedComponents.Add(new ExtendedComponent
                {
                    Name = "ViaBill Payments",
                    ScriptUrl = "/js/ViaBillAdminTab/ViaBillAdminTab.js?v=2",
                    Order = 100,
                    ComponentLocation = ComponentLocation.Tab,
                    OrderTypes = OrderTypes.PurchaseOrder
                });
            });
        }

        public void Initialize(InitializationEngine context) { }
        public void Uninitialize(InitializationEngine context) { }
    }
}