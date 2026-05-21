using System;
using System.Collections.Generic;

namespace Berichtsportal.Models;

public partial class User
{
    public Guid UserId { get; set; }

    public string Email { get; set; } = null!;

    public string FirstName { get; set; } = null!;

    public string LastName { get; set; } = null!;

    public int RoleId { get; set; }

    public DateTime? AusbildungBeginn { get; set; }
    public DateTime? AusbildungEnde { get; set; }

    public Guid? MentorId { get; set; }

    public virtual ICollection<Comment> Comments { get; set; } = new List<Comment>();

    public virtual ICollection<User> InverseMentor { get; set; } = new List<User>();

    public virtual User? Mentor { get; set; }

    public virtual ICollection<Report> Reports { get; set; } = new List<Report>();

    public virtual Role Role { get; set; } = null!;
}
