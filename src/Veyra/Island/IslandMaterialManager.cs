using System;
using System.Windows;
using System.Windows.Media;

namespace GlassDock.Island;

/// <summary>
/// 灵动岛屿材质管理器
/// </summary>
public static class IslandMaterialManager
{
    private static MaterialType _currentMaterial = MaterialType.Acrylic;

    public static MaterialType CurrentMaterial
    {
        get => _currentMaterial;
        set
        {
            _currentMaterial = value;
            MaterialChanged?.Invoke(null, value);
        }
    }

    public static event EventHandler<MaterialType>? MaterialChanged;

    /// <summary>
    /// 获取材质的背景画刷
    /// </summary>
    public static Brush GetBackgroundBrush(MaterialType material)
    {
        return material switch
        {
            MaterialType.LiquidGlass => CreateLiquidGlassBrush(),
            MaterialType.Acrylic => CreateAcrylicBrush(),
            MaterialType.Glass => CreateGlassBrush(),
            MaterialType.FrostedGlass => CreateFrostedGlassBrush(),
            _ => CreateAcrylicBrush()
        };
    }

    /// <summary>
    /// 苹果液态玻璃效果
    /// </summary>
    private static Brush CreateLiquidGlassBrush()
    {
        return new LinearGradientBrush
        {
            GradientStops = new GradientStopCollection
            {
                new GradientStop(Color.FromArgb(160, 245, 245, 250), 0),      // 顶部：接近白色的半透明
                new GradientStop(Color.FromArgb(140, 220, 225, 235), 0.3),    // 中上：淡蓝灰
                new GradientStop(Color.FromArgb(120, 200, 210, 230), 0.6),    // 中下：更深的蓝灰
                new GradientStop(Color.FromArgb(100, 180, 195, 220), 1)       // 底部：深蓝灰
            },
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1)
        };
    }

    /// <summary>
    /// 亚克力效果（原始 Veyra 风格）
    /// </summary>
    private static Brush CreateAcrylicBrush()
    {
        return new LinearGradientBrush
        {
            GradientStops = new GradientStopCollection
            {
                new GradientStop(Color.FromArgb(140, 119, 157, 211), 0),
                new GradientStop(Color.FromArgb(100, 176, 206, 249), 0.6),
                new GradientStop(Color.FromArgb(65, 213, 236, 255), 1)
            }
        };
    }

    /// <summary>
    /// 标准玻璃效果
    /// </summary>
    private static Brush CreateGlassBrush()
    {
        return new LinearGradientBrush
        {
            GradientStops = new GradientStopCollection
            {
                new GradientStop(Color.FromArgb(120, 255, 255, 255), 0),
                new GradientStop(Color.FromArgb(80, 240, 245, 255), 0.5),
                new GradientStop(Color.FromArgb(60, 220, 230, 250), 1)
            }
        };
    }

    /// <summary>
    /// 磨砂玻璃效果
    /// </summary>
    private static Brush CreateFrostedGlassBrush()
    {
        return new SolidColorBrush(Color.FromArgb(180, 240, 240, 245));
    }

    /// <summary>
    /// 获取边框画刷
    /// </summary>
    public static Brush GetBorderBrush(MaterialType material)
    {
        return material switch
        {
            MaterialType.LiquidGlass => new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)),
            MaterialType.Acrylic => new SolidColorBrush(Color.FromArgb(92, 247, 252, 255)),
            MaterialType.Glass => new SolidColorBrush(Color.FromArgb(100, 255, 255, 255)),
            MaterialType.FrostedGlass => new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)),
            _ => new SolidColorBrush(Color.FromArgb(92, 247, 252, 255))
        };
    }

    /// <summary>
    /// 获取材质的圆角半径
    /// </summary>
    public static CornerRadius GetCornerRadius(MaterialType material)
    {
        return material switch
        {
            MaterialType.LiquidGlass => new CornerRadius(26),  // 液态玻璃更圆润
            MaterialType.Acrylic => new CornerRadius(23),
            MaterialType.Glass => new CornerRadius(20),
            MaterialType.FrostedGlass => new CornerRadius(18),
            _ => new CornerRadius(23)
        };
    }
}
