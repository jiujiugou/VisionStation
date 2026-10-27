using System.Windows;

// WPF 主题资源查找策略（模板生成的程序集特性，一般不用改）：
//   ResourceDictionaryLocation.None          —— 不按"页面级"主题字典查找；
//   ResourceDictionaryLocation.SourceAssembly —— 通用主题字典放在本程序集内。
// 本项目没有自定义主题字典，段内颜色都写在 XAML 的局部样式里，所以这里保持默认。
[assembly:ThemeInfo(
    ResourceDictionaryLocation.None,            //where theme specific resource dictionaries are located
                                                //(used if a resource is not found in the page,
                                                // or application resource dictionaries)
    ResourceDictionaryLocation.SourceAssembly   //where the generic resource dictionary is located
                                                //(used if a resource is not found in the page,
                                                // app, or any theme specific resource dictionaries)
)]
