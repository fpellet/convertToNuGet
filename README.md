# ConvertToNuGet

Allows to convert dll to nuget package and not add dll file in repository.

This can be useful if you use DevExpress for example. Indeed, it only provides dlls.

This project is inspired by https://github.com/caioproiete/DevExpress-NuGet and https://gist.github.com/smoothdeveloper/994522269a0a04c275c9

## Usage

```
run.bat <dll directory> [output=<directory output of nuget packages>] [culture=<culture name> cultureVersion=<version of package base>]
```

output, culture and cultureVersion are optional.

Default value of output is "./nugetpackages"

Default value of culture and cultureVersion is empty. It's required only if dll directory contains dll with resources (file name end by .resources.dll). cultureVersion is very important because nuget use name convention.
For example, if your dll is MyProject.dll with version 14.2.5.0, then you should create MyProject.fr.14.2.5.0 nuget package for fr resources. (https://docs.nuget.org/ndocs/create-packages/creating-localized-packages)


## License

The MIT License (MIT)

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
