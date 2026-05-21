using Berichtsportal.Enums;
using System;
using System.Collections.Generic;

namespace Berichtsportal.Models;

public partial class Report
{
    public int ReportId { get; set; }

    public Guid AzubiId { get; set; }

    public int CalendarWeek { get; set; }

    public int Year { get; set; }

    public string? Content { get; set; }

    public ReportStatus Status { get; set; }

    public DateTime? LastModified { get; set; }

    public virtual User Azubi { get; set; } = null!;

    public virtual ICollection<Comment> Comments { get; set; } = new List<Comment>();
}
