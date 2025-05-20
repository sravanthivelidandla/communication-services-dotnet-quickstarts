
using Azure.Data.Tables;
using Azure;
using System;
using System.Threading.Tasks;

    public class TableStorageService
    {
        private readonly TableClient _tableClient;

        public TableStorageService(string storageConnectionString, string tableName)
        {
            var serviceClient = new TableServiceClient(storageConnectionString);
            _tableClient = serviceClient.GetTableClient(tableName);
            _tableClient.CreateIfNotExists();
        }

        public async Task AddPrescriptionAsync(PrescriptionEntity prescription)
        {
            await _tableClient.AddEntityAsync(prescription);
        }

    public async Task<PrescriptionEntity> GetPrescriptionAsync(string prescriptionId)
    {
        string propertyName = "PrescriptionId";
        var response = _tableClient.Query<PrescriptionEntity>(filter: $"{propertyName} eq '{prescriptionId}'");

        var entity = response.FirstOrDefault();
        if (entity == null)
        {
            return null;
        }

        return new PrescriptionEntity
        {
            PartitionKey = entity.PartitionKey,
            RowKey = entity.RowKey,
            Timestamp = entity.Timestamp,
            ETag = entity.ETag,
            DOB = entity.DOB,
            Name = entity.Name,
            DrugName = entity.DrugName,
            PrescriptionId = entity.PrescriptionId,
            RefillsPending = entity.RefillsPending,
            TotalRefills = entity.TotalRefills,
            RecordId = entity.RecordId,
            ScreenPopUpUrl = entity.ScreenPopUpUrl,
            PhoneNumber = entity.PhoneNumber
            // Map other properties from entity to PrescriptionEntity
        };
    }
    }



