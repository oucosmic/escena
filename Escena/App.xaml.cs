using System.Globalization;
using System.Windows;
using System.Windows.Markup;

namespace Escena;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Que els números es mostrin i s'escriguin amb el format regional (p. ex. 1,5).
        FrameworkElement.LanguageProperty.OverrideMetadata(
            typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag)));

        // Idioma de la interfície (abans de crear cap finestra).
        L.Init(AppSettings.Load().Language);

        // Enmig d'una funció, millor avisar que tancar el programa.
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show("S'ha produït un error inesperat:\n\n" + args.Exception.Message,
                "Escena", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        base.OnStartup(e);
    }
}
