using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
namespace RevitAPI_Testing.Forms
{
    public partial class TextTypeDataWindow : Window
    {
        public List<TextTypeData> TextTypeDataList { get; set; }

        public TextTypeDataWindow(List<TextTypeData> textTypesParameters)
        {
            InitializeComponent();

            // Set the TextTypeDataList to the provided list
            TextTypeDataList = textTypesParameters;

            // Set the DataContext to enable data binding
            DataContext = this;
        }
    }
}



