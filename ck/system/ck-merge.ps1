# Read the contents of the first JSON file
$json1 = Get-Content -Raw -Path "./ck-associations.json" | ConvertFrom-Json

# Read the contents of the second JSON file
$json2 = Get-Content -Raw -Path "./ck-attributes.json" | ConvertFrom-Json

# Read the content of entities
$json3 = Get-Content -Raw -Path "./ck-equipment.json" | ConvertFrom-Json
$json4 = Get-Content -Raw -Path "./ck-asset.json" | ConvertFrom-Json
$json5 = Get-Content -Raw -Path "./ck-communication.json" | ConvertFrom-Json
$json6 = Get-Content -Raw -Path "./ck-plug.json" | ConvertFrom-Json
$json7 = Get-Content -Raw -Path "./ck-socket.json" | ConvertFrom-Json
$json8 = Get-Content -Raw -Path "./ck-events.json" | ConvertFrom-Json
$jsonEntities = $json3.entities + $json4.entities + $json5.entities + $json6.entities + $json7.entities + $json8.entities

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
$mergedJsonString | Out-File -FilePath "./ck-system.json"