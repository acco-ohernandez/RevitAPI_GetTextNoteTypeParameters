#region Namespaces
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Windows.Forms;

using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

using RevitAPI_Testing.Forms;
//using Forms = System.Windows.Forms;
using Forms = System.Windows.Forms.IWin32Window;
using Timer = System.Timers.Timer;
#endregion

namespace RevitAPI_Testing
{
    [Transaction(TransactionMode.Manual)]
    public class Command1 : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            // this is a variable for the Revit application
            UIApplication uiapp = commandData.Application;

            // this is a variable for the current Revit model
            Document doc = uiapp.ActiveUIDocument.Document;

            // Temporary Dialog Box
            var m = $" This could be a short message \n" +
                    $" Or \n" +
                    $" It  \n" +
                    $" Could \n" +
                    $" Be \n" +
                    $" A MultiLine \n" +
                    $" Message \n";
            InfoForm.TempDialog(2, m);

            // Get all text note types

            var textNoteTypes = new FilteredElementCollector(doc)
                                .OfClass(typeof(TextNoteType))
                                .Cast<TextNoteType>()
                                .OrderBy(x => x.Name)
                                .ToList();
            // Now, textNoteTypes contains all text note types in the document
            // Define a list to hold text type data
            List<TextTypeData> textTypesParameters = new List<TextTypeData>();

            int c = 1;
            foreach (TextNoteType textNoteType in textNoteTypes)
            {
                // Create an instance of TextTypeData for the current text note type
                TextTypeData textTypeData = new TextTypeData()
                {
                    FamilyName = textNoteType.FamilyName,
                    TypeName = textNoteType.Name,
                    Color = GetColorFromParameter(textNoteType),
                    LineWeight = GetLineWeightFromParameter(textNoteType),
                    Background = GetBackGroundFromParameter(textNoteType),
                    ShowBorder = GetShowBorder(textNoteType),
                    LeaderBorderOffset = GetLeaderBorderOffset(textNoteType),
                    LeaderArrowhead = GetLeaderArrowHead(textNoteType),
                    Bold = GetIsBold(textNoteType),
                    Italic = textNoteType.get_Parameter(BuiltInParameter.TEXT_STYLE_ITALIC).AsInteger() == 1 ? "Yes" : "No",
                    Underline = GetIsUnderlined(textNoteType),
                    //    WidthFactor = textNoteType.get_Parameter(BuiltInParameter.TEXT_WIDTH_SCALE).IsDetermined ? textNoteType.get_Parameter(BuiltInParameter.TEXT_WIDTH_SCALE).AsString() : "Not Determined"
                    WidthFactor = GetWidthFactor(textNoteType)
                };

                // Add the TextTypeData instance to the list
                textTypesParameters.Add(textTypeData);

                //// Process each text note type as needed
                //Debug.Print($"{c}. Text Note Type: {textNoteType.Name}");

                //    // Iterate through parameters of the current text note type
                //    foreach (Parameter param in textNoteType.Parameters)
                //    {
                //        // Access parameter properties as needed
                //        string paramName = param.Definition.Name;
                //        string paramValue;

                //        // Use a switch to handle different StorageTypes
                //        switch (param.StorageType)
                //        {
                //            case StorageType.String:
                //                paramValue = param.AsString();
                //                break;
                //            case StorageType.Integer:
                //                paramValue = param.AsInteger().ToString();
                //                break;
                //            case StorageType.Double:
                //                paramValue = param.AsDouble().ToString();
                //                break;
                //            case StorageType.ElementId:
                //                paramValue = param.AsElementId().IntegerValue.ToString();
                //                break;
                //            default:
                //                paramValue = "(Unsupported StorageType)";
                //                break;
                //        }

                //        // Print parameter information
                //        Debug.Print($"   - Parameter: {paramName} -----------> Value: {paramValue}");
                //    }

                //    c++;
            }


            // 
            ////List<TextTypeData> textTypesParameters = new List<TextTypeData>();

            ////int c = 1;
            ////foreach (TextNoteType textNoteType in textNoteTypes)
            ////{
            ////    // Process each text note type as needed
            ////    // You can access type properties, parameters, etc.
            ////    Debug.Print($"{c}. Text Note Type: {textNoteType.Name}");

            ////    // Iterate through parameters of the current text note type
            ////    foreach (Parameter param in textNoteType.Parameters)
            ////    {
            ////        // Access parameter properties as needed
            ////        string paramName = param.Definition.Name;
            ////        string paramValue;

            ////        // Use a switch to handle different StorageTypes
            ////        switch (param.StorageType)
            ////        {
            ////            case StorageType.String:
            ////                paramValue = param.AsString();
            ////                break;
            ////            case StorageType.Integer:
            ////                paramValue = param.AsInteger().ToString();
            ////                break;
            ////            case StorageType.Double:
            ////                paramValue = param.AsDouble().ToString();
            ////                break;
            ////            case StorageType.ElementId:
            ////                paramValue = param.AsElementId().IntegerValue.ToString();
            ////                break;
            ////            default:
            ////                paramValue = "(Unsupported StorageType)";
            ////                break;
            ////        }

            ////        // Print parameter information
            ////        Debug.Print($" - Parameter: {paramName} -----------> Value: {paramValue}");

            ////        // ChatGPT add the parameter values to the textTypesParameters List
            ////    }

            ////    c++;
            ////}


            return Result.Succeeded;
        }

        private string GetWidthFactor(TextNoteType textNoteType)
        {
            // Find the 'Width Factor' parameter by name
            Parameter widthFactorParam = textNoteType.Parameters.Cast<Parameter>().FirstOrDefault(p => p.Definition.Name == "Width Factor");

            if (widthFactorParam != null)
            {
                // Check if the parameter value is determined (not a placeholder)
                if (widthFactorParam.HasValue)
                {
                    // Return the width factor as a string
                    return widthFactorParam.AsValueString();
                }
                else
                {
                    return "Not Determined";
                }
            }

            // Return an error message if 'Width Factor' parameter is not found
            return "N/A.";
        }

        private string GetIsUnderlined(TextNoteType textNoteType)
        {
            // Find the 'Underline' parameter by name
            Parameter underlineParam = textNoteType.Parameters.Cast<Parameter>().FirstOrDefault(p => p.Definition.Name == "Underline");

            if (underlineParam != null)
            {
                // Check if the parameter value is 1 (indicating underline) or not
                int underlineValue = underlineParam.AsInteger();

                // Convert the integer value to a more human-readable "Yes" or "No"
                string underlineStatus = underlineValue == 1 ? "Yes" : "No";

                return underlineStatus;
            }

            // Return an error message if 'Underline' parameter is not found
            return "N/A.";
        }

        private string GetIsBold(TextNoteType textNoteType)
        {
            // Find the 'Bold' parameter by name
            Parameter boldParam = textNoteType.Parameters.Cast<Parameter>().FirstOrDefault(p => p.Definition.Name == "Bold");

            if (boldParam != null)
            {
                // Check if the parameter value is 1 (indicating bold) or not
                int boldValue = boldParam.AsInteger();

                // Convert the integer value to a more human-readable "Yes" or "No"
                string boldStatus = boldValue == 1 ? "Yes" : "No";

                return boldStatus;
            }

            // Return an error message if 'Bold' parameter is not found
            return "N/A.";
        }

        private string GetLeaderArrowHead(TextNoteType textNoteType)
        {
            // Find the 'Leader Arrowhead' parameter by name
            Parameter arrowheadParam = textNoteType.Parameters.Cast<Parameter>().FirstOrDefault(p => p.Definition.Name == "Leader Arrowhead");

            if (arrowheadParam != null)
            {
                // Check if the parameter has a value (not a placeholder)
                if (arrowheadParam.HasValue)
                {
                    // Return the leader arrowhead value as a string
                    return arrowheadParam.AsValueString();
                }
                else
                {
                    return "Not Determined";
                }
            }

            // Return an error message if 'Leader Arrowhead' parameter is not found
            return "N/A.";
        }

        private string GetLeaderBorderOffset(TextNoteType textNoteType)
        {
            // Find the 'Leader Border Offset' parameter by name
            Parameter leaderOffsetParam = textNoteType.Parameters.Cast<Parameter>().FirstOrDefault(p => p.Definition.Name == "Leader/Border Offset");

            if (leaderOffsetParam != null)
            {
                // Check if the parameter value is determined (not a placeholder)
                if (leaderOffsetParam.HasValue)
                {
                    // Return the leader border offset as a string
                    return leaderOffsetParam.AsValueString();
                }
                else
                {
                    return "Not Determined";
                }
            }

            // Return an error message if 'Leader Border Offset' parameter is not found
            return "N/A.";
        }

        private string GetShowBorder(TextNoteType textNoteType)
        {
            // Find the 'Show Border' parameter by name
            Parameter showBorderParam = textNoteType.Parameters.Cast<Parameter>().FirstOrDefault(p => p.Definition.Name == "Show Border");

            if (showBorderParam != null)
            {
                // Check if the parameter value is true or false
                bool showBorder = showBorderParam.AsInteger() == 1;

                return showBorder ? "Yes" : "No";
            }

            // Return an error message if 'Show Border' parameter is not found
            return "N/A.";
        }

        private string GetBackGroundFromParameter(TextNoteType textNoteType)
        {
            // Find the 'Background' parameter by name
            Parameter backgroundParam = textNoteType.Parameters.Cast<Parameter>().FirstOrDefault(p => p.Definition.Name == "Background");

            if (backgroundParam != null)
            {
                // Check if the parameter value is true or false
                bool isBackground = backgroundParam.AsInteger() == 1;

                return isBackground ? "Transparent" : "Opaque";
            }

            // Return an error message if 'Background' parameter is not found
            return "N/A.";
        }

        private string GetLineWeightFromParameter(TextNoteType textNoteType)
        {
            // Find the 'Line Weight' parameter by name
            Parameter lineWeightParam = textNoteType.Parameters.Cast<Parameter>().FirstOrDefault(p => p.Definition.Name == "Line Weight");

            if (lineWeightParam != null)
            {
                int lineWeightInt = lineWeightParam.AsInteger();

                //// Map known line weight values to their names
                //Dictionary<int, string> lineWeightMap = new Dictionary<int, string>
                //{
                //    { -1, "Thin" },
                //    { 0, "Medium" },
                //    { 1, "Thick" },
                //    // Add more line weight mappings here as needed
                //};
                //// Check if the line weight is in the mapping
                //if (lineWeightMap.TryGetValue(lineWeightInt, out string lineWeightName))
                //{
                //    return lineWeightName;
                //}

                // If the line weight is not in the mapping, return its integer value
                return lineWeightInt.ToString();
            }

            // Return an error message if 'Line Weight' parameter is not found
            return "N/A.";
        }

        /// <summary>
        /// Retrieves the color representation of a TextNoteType's 'Color' parameter.
        /// </summary>
        /// <param name="textNoteType">The TextNoteType to retrieve the color from.</param>
        /// <returns>The color value as an RGB string (e.g., "RGB(255, 0, 0)" for red) 
        /// or "Black" if it's black, 
        /// or "Red" if it's black,
        /// or "Blue"if it's black,
        /// or "Green" if it's black, 
        /// or "Gray"if it's black, 
        /// or "Yellow" if it's black,
        /// or an error message if 'Color' parameter is not found.</returns>
        private string GetColorFromParameter(TextNoteType textNoteType)
        {
            // Find the 'Color' parameter by name
            Parameter colorParam = textNoteType.Parameters.Cast<Parameter>().FirstOrDefault(p => p.Definition.Name == "Color");

            if (colorParam != null)
            {
                int colorInt = colorParam.AsInteger();

                // Map known colors to their names
                Dictionary<System.Drawing.Color, string> colorMap = new Dictionary<System.Drawing.Color, string>
                {
                    { System.Drawing.Color.Black, "Black" },
                    { System.Drawing.Color.Red,   "Red"   },
                    { System.Drawing.Color.Blue,  "Blue"  },
                    { System.Drawing.Color.Green, "Green" },
                    { System.Drawing.Color.Gray,  "Gray"   },
                    { System.Drawing.Color.Yellow,"Yellow" },
                    // Add more color mappings here as needed
                };

                // Check if the color is in the mapping
                if (colorMap.TryGetValue(System.Drawing.ColorTranslator.FromOle(colorInt), out string colorName))
                {
                    return colorName;
                }

                // If the color is not in the mapping, return the RGB value
                System.Drawing.Color dotNetColor = System.Drawing.ColorTranslator.FromOle(colorInt);
                return $"RGB({dotNetColor.R}, {dotNetColor.G}, {dotNetColor.B})";
            }

            // Return an error message if 'Color' parameter is not found
            return "N/A.";
        }

        internal static PushButtonData GetButtonData()
        {
            // use this method to define the properties for this command in the Revit ribbon
            string buttonInternalName = "btnCommand1";
            string buttonTitle = "Button 1";

            ButtonDataClass myButtonData1 = new ButtonDataClass(
                buttonInternalName,
                buttonTitle,
                MethodBase.GetCurrentMethod().DeclaringType?.FullName,
                Properties.Resources.Blue_32,
                Properties.Resources.Blue_16,
                "This is a tooltip for Button 1");

            return myButtonData1.Data;
        }
    }
    // Other classes
    //...
    // Define a class to hold the text type data
    public class TextTypeData
    {
        public string FamilyName { get; set; }
        public string TypeName { get; set; }
        public string Color { get; set; }
        public string LineWeight { get; set; }
        public string Background { get; set; }
        public string ShowBorder { get; set; }
        public string LeaderBorderOffset { get; set; }
        public string LeaderArrowhead { get; set; }
        public string Bold { get; set; }
        public string Italic { get; set; }
        public string Underline { get; set; }
        public string WidthFactor { get; set; }
    }

}
