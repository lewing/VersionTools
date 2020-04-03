#!/bin/bash

PACKAGE_PATH="~/.nuget/packages/microsoft.aspnetcore.components.webassembly.runtime/$3/tools/dotnetwasm/"

wget https://jenkins.mono-project.com/job/test-mono-mainline-wasm/$build/label=ubuntu-1804-amd64/Azure/processDownloadRequest/$1/ubuntu-1804-amd64/sdks/wasm/mono-wasm-$2.zip

unzip mono-wasm-$2.zip
cd mono-wasm-$2

rm -rf $PACKAGE_PATH/bcl
mv wasm-bcl $PACKAGE_PATH/bcl

rm -rf $PACKAGE_PATH/framework
mv framework $PACKAGE_PATH/framework

rm -rf $PACKAGE_PATH/wasm/*
mv builds/release/dotnet.* $PACKAGE_PATH/wasm/

cd ..
rm mono-wasm-$2.zip
rm -rf mono-wasm-$2