using System;
using System.Collections.Generic;

namespace Berichtsportal.Models;

public partial class Comment
{
    public int CommentId { get; set; }

    public int ReportId { get; set; }

    public Guid CommentorId { get; set; }

    public string CommentText { get; set; } = null!;

    public DateTime? CreatedAt { get; set; }

    public virtual User Commentor { get; set; } = null!;

    public virtual Report Report { get; set; } = null!;
}
