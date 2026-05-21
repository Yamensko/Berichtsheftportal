namespace Berichtsportal.Enums
{
    // Die Klasse MUSS 'static' sein
    public static class ReportStatusExtensions
    {
        // Das 'this' vor dem ersten Parameter ist der Zaubertrick. 
        // Es sagt C#: "Füge diese Methode direkt an ReportStatus an."
        public static string ToCssClass(this ReportStatus status)
        {
            return status switch
            {
                ReportStatus.Submitted => "submitted",
                ReportStatus.Approved => "approved",
                ReportStatus.Rejected => "rejected",
                _ => "draft"
            };
        }
    }
}