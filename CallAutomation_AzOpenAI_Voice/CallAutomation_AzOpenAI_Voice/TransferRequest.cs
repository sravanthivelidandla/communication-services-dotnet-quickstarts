using System.ComponentModel.DataAnnotations;

public class TransferRequest
{
    public string CallConnectionId { get; set; }
    public string TargetPhoneNumber { get; set; }
}


public class AddParticipantRequest
{
    [Required]
    public string CallConnectionId { get; set; }

    [Required]
    public string TargetPhoneNumber { get; set; }
}
