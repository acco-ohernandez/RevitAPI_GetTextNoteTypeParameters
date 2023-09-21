using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;


namespace RevitAPI_Testing.Forms
{
    /// <summary>
    /// Interaction logic for InfoForm.xaml
    /// </summary>
    public partial class InfoForm : Window
    {
        public InfoForm()
        {
            InitializeComponent();
        }
        /// <summary>
        /// Displays a temporary information dialog with an optional message for a specified duration.
        /// </summary>
        /// <param name="numOfSeconds">The duration in seconds for which the dialog should be displayed.</param>
        /// <param name="temporaryMessage">An optional message to display in the dialog. If empty, a default message is shown.</param>
        /// <remarks>
        /// Example 1: To show a dialog with a custom message for 5 seconds:
        /// <code>
        /// InfoForm.TempDialog(5, "My custom message here.");
        /// </code>
        /// Example 2: To show a dialog with the default "Empty Message Box" for 3 seconds:
        /// <code>
        /// InfoForm.TempDialog(3);
        /// </code>
        /// </remarks>
        internal static void TempDialog(int numOfSeconds, string temporaryMessage = "")
        {
            try
            {
                var newForm = new InfoForm()
                {
                    //Width = 350,
                    //Height = 200,
                    SizeToContent = System.Windows.SizeToContent.WidthAndHeight,
                    WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen,
                    Topmost = true,
                };
                // if temporaryMessage is Empty ("") set the yourForm.lbl_Message.Content = "Empty Message Box"
                newForm.lbl_Message.Content = string.IsNullOrEmpty(temporaryMessage) ? "Empty Message Box" : temporaryMessage;
                newForm.lbl_Message.Padding = new Thickness(5);
                newForm.lbl_Message.Margin = new Thickness(5);

                newForm.Show();
                Task.Run(async () =>
                {
                    await Task.Delay(numOfSeconds * 1000);

                    // Close the form on the UI thread using Dispatcher.Invoke
                    newForm.Dispatcher.Invoke(() =>
                    {
                        newForm.Close();
                    });
                });
            }
            catch (Exception ex)
            {
                // Handle any exceptions and show an error message
                MessageBox.Show($"An error occurred: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

    }
}
