// -----------------------------------------------------------------------
//  GhDocumentProvider.cs
//  Single entry point for the active GH_Document.
//  Everything in GhInspector flows from here.
// -----------------------------------------------------------------------

using Grasshopper;
using Grasshopper.Kernel;

namespace WASPer_3DP.AI
{
    public static class GhDocumentProvider
    {
        /// <summary>
        /// Returns the currently active Grasshopper document,
        /// or null if Grasshopper has no open document or the editor is not ready.
        /// </summary>
        public static GH_Document GetActiveDocument()
        {
            // Instances.ActiveDocument is the canonical GH API for this.
            // DocumentEditor.Document does not compile — Document is not exposed
            // on GH_DocumentEditor directly; ActiveDocument handles the null case.
            return Instances.ActiveDocument;
        }
    }
}
