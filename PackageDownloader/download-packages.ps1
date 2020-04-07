param ($filepath, $url, $runtime)
$PACKAGE_PATH="~\\.nuget\\packages\\microsoft.aspnetcore.components.webassembly.runtime\\$runtime\\tools\\dotnetwasm"

if ($null -eq $filepath) {
	Invoke-WebRequest -Uri $url -OutFile wasm-package.zip -UseBasicParsing

	Expand-Archive wasm-package.zip 
	cd wasm-package
}
else {
	cd $filepath
}

rm -r $PACKAGE_PATH\bcl\*
cp -r wasm-bcl\wasm\* $PACKAGE_PATH\bcl

rm -r $PACKAGE_PATH\framework
cp -r framework $PACKAGE_PATH\framework

rm -r $PACKAGE_PATH\wasm\*
cp -r builds\release\dotnet.js $PACKAGE_PATH\wasm\dotnet.$runtime.js
cp -r builds\release\dotnet.wasm $PACKAGE_PATH\wasm\

cd ..
rm wasm-package.zip
rm -r wasm-package