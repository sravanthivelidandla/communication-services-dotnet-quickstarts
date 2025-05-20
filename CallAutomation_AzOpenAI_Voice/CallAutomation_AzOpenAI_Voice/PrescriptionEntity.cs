using System.ComponentModel.DataAnnotations;
using Azure;
using Azure.Data.Tables;

public class PrescriptionEntity : ITableEntity
{
    public string PartitionKey { get; set; }
    public string RowKey { get; set; }
    public string UserId { get; set; }
    public string PrescriptionId { get; set; }
    public string DrugName { get; set; }
    public string TotalRefills { get; set; }
    public string RefillsPending { get; set; }
    public string PhoneNumber { get; set; }
    public string DOB { get; set; }
    public ETag ETag { get; set; }
    public DateTimeOffset? Timestamp { get; set; }

    public string RecordId { get; set; }

    public string ScreenPopUpUrl { get; set; }

    public string Name { get; set; }
}

public class ValidatePrescriptionRequest
{
    [Required]
    public string prescriptionId { get; set; }
}

public class ValidatePrescriptionResponse
{
    public string Message { get; set; }
    public PrescriptionDetails PrescriptionDetails { get; set; }
    public bool IsSuccess { get; set; }
}

public class PrescriptionDetails
{
    public string UserId { get; set; }
    public string PrescriptionId { get; set; }
    public string DrugName { get; set; }
    public string TotalRefills { get; set; }
    public string RefillsPending { get; set; }
    public string PhoneNumber { get; set; }
    public string DOB { get; set; }
    public string Name { get;set; }

    public string RecordId { get; set; }

    public string ScreenPopUpUrl { get; set; }
}