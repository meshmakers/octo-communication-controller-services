# Read the contents of the first JSON file
$json2 = Get-Content -Raw -Path "./ck-attributes.json" | ConvertFrom-Json

# Read the content of entities
$json5 = Get-Content -Raw -Path "./pv/ck-pv.json" | ConvertFrom-Json
$jsonEntities = $json5.entities

# Create a new object and combine the properties from each JSON
Write-Host "Merging JSON files..."
$mergedJson = [PSCustomObject]@{
    associationRoles = $json1.associationRoles
    attributes = $json2.attributes
    entities = $jsonEntities
}

# Convert the merged JSON back to a string
Write-Host "Converting to JSON..."
$mergedJsonString = $mergedJson | ConvertTo-Json -Depth 10

# Save the merged JSON to a new file
Write-Host "Saving merged JSON to file..."
$mergedJsonString | Out-File -FilePath "./ck-application.json"