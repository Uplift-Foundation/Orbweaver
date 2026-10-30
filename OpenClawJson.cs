using System.Text.Json;

namespace Orbweaver
{
    // Reading a string out of JSON a gateway sent.
    //
    // JsonElement.GetString() throws InvalidOperationException when the string
    // holds a lone surrogate escape ("\ud800"), which is valid JSON text and so
    // parses fine. A hostile or merely corrupt gateway can put one in any string
    // it sends, and a throw out of the sessions poll or the event pump stalls
    // every orb at once. Reading through this instead makes such a string absent:
    // the field is dropped, the row and its neighbours are not.
    internal static class OpenClawJson
    {
        internal static string? SafeString(this JsonElement element)
        {
            try
            {
                return element.GetString();
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }
}
