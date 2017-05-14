param([String]$key,[String]$version)

function setProjectVersion([String]$project, [String]$version) {
	$fileName =  ".\src\$project\$project.xproj"
    $content = (Get-Content $fileName) -join "`n" | ConvertFrom-Json
    $content.version = $version
    $newContent = ConvertTo-Json -Depth 10 $content
    Set-Content $fileName $newContent
}

function publishProject([String]$project,[String]$version) {
	cd ".\src\$project"
	& dotnet pack -c Release
	$file = Get-Item "bin\Release\*.$version.nupkg"
	nuget push $file.FullName $key -Source https://api.nuget.org/v3/index.json
	cd ..\..
}

if ($version -ne "") {
	setProjectVersion "Folke.Identity.Server" $version
	
	& dotnet restore

	publishProject "Folke.Identity.Server" $version
}
