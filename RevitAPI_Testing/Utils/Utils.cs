using System;
using System.Collections.Generic;
using System.Linq;

using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace RevitAPI_Testing
{
    internal static class Utils
    {
        internal static RibbonPanel CreateRibbonPanel(UIControlledApplication app, string tabName, string panelName)
        {
            RibbonPanel currentPanel = GetRibbonPanelByName(app, tabName, panelName);

            if (currentPanel == null)
                currentPanel = app.CreateRibbonPanel(tabName, panelName);

            return currentPanel;
        }

        internal static RibbonPanel GetRibbonPanelByName(UIControlledApplication app, string tabName, string panelName)
        {
            foreach (RibbonPanel tmpPanel in app.GetRibbonPanels(tabName))
            {
                if (tmpPanel.Name == panelName)
                    return tmpPanel;
            }

            return null;
        }

        /// <summary>
        /// Ensures there is a selection in the UIDocument, or prompts the user to select elements.
        /// </summary>
        /// <param name="uidoc"></param>
        /// <param name="userCanceled"></param>
        /// <param name="errorMessage"></param>
        /// <param name="selectedElementIds"></param>
        /// <returns></returns>
        public static bool? TryEnsureSelectionOrPrompt(
             UIDocument uidoc,
             out bool userCanceled,
             out string errorMessage,
             out IList<ElementId> selectedElementIds)
        {
            userCanceled = false;
            errorMessage = string.Empty;
            selectedElementIds = null;

            var current = uidoc.Selection.GetElementIds();
            if (current != null && current.Count > 0)
            {
                selectedElementIds = current.ToList();
                return true;
            }

            try
            {
                IList<Reference> picked =
                  uidoc.Selection.PickObjects(ObjectType.Element, "Please select elements");

                if (picked != null && picked.Count > 0)
                {
                    selectedElementIds = picked.Select(r => r.ElementId).ToList();
                    uidoc.Selection.SetElementIds(selectedElementIds);
                    return true;
                }

                errorMessage = "No elements were selected.";
                return false;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                userCanceled = true;
                errorMessage = string.Empty;   // keep empty to avoid Revit warning on Cancelled
                selectedElementIds = null;
                return null;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                selectedElementIds = null;
                return false;
            }
        }


    }
}
