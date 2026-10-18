using GymMembership.App.Controls;
using Microsoft.Maui.Controls;

public class PhonePageTemplateTests
{
    [Fact]
    public void Direct_type_template_cannot_construct_a_page_requiring_a_view_model()
    {
        var template = new DataTemplate(typeof(DependencyPage));
        Assert.Throws<MissingMethodException>(() => template.CreateContent());
    }

    [Fact]
    public void More_page_factory_uses_registered_services_and_preserves_injected_view_model()
    {
        var vm = new object();
        var calls = 0;
        var services = new TestServices(type =>
        {
            Assert.Equal(typeof(DependencyPage), type);
            calls++;
            return new DependencyPage(vm);
        });
        var template = PhonePageTemplates.ForService(services, typeof(DependencyPage));
        Assert.Equal(0, calls);
        var page = Assert.IsType<DependencyPage>(template.CreateContent());
        Assert.Same(vm, page.BindingContext);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Missing_registration_is_reported_instead_of_falling_back_to_a_parameterless_constructor()
    {
        var template = PhonePageTemplates.ForService(new TestServices(_ => null), typeof(DependencyPage));
        Assert.Throws<InvalidOperationException>(() => template.CreateContent());
    }

    public sealed class DependencyPage : ContentPage
    {
        public DependencyPage(object vm) => BindingContext = vm;
    }

    sealed class TestServices(Func<Type, object?> resolve) : IServiceProvider
    {
        public object? GetService(Type serviceType) => resolve(serviceType);
    }
}
