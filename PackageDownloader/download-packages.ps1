$build=$args[0]
$commit=$args[1]
$runtime=$args[2]

$PACKAGE_PATH="~\\.nuget\\packages\\microsoft.aspnetcore.components.webassembly.runtime\\$runtime\\tools\\dotnetwasm\\"

Invoke-WebRequest -Uri https://jenkins.mono-project.com/job/test-mono-mainline-wasm/$build/label=ubuntu-1804-amd64/Azure/processDownloadRequest/$build/ubuntu-1804-amd64/sdks/wasm/mono-wasm-$commit.zip -OutFile wasm-package.zip -UseBasicParsing

Expand-Archive wasm-package.zip 
cd wasm-package

rm -r $PACKAGE_PATH\bcl
mv wasm-bcl $PACKAGE_PATH\bcl

rm -r $PACKAGE_PATH\framework
mv framework $PACKAGE_PATH\framework

rm -r $PACKAGE_PATH\wasm\*
mv builds\release\dotnet.* $PACKAGE_PATH\wasm\

cd ..
rm wasm-package.zip
rm -r wasm-package