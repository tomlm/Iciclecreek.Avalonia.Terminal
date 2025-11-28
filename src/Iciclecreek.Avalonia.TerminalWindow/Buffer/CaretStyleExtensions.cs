
namespace Iciclecreek.Avalonia.TerminalWindow.Buffer
{
    internal static class CaretStyleExtensions
    {
        public static CaretStyle Blend(this CaretStyle pixelBelow, CaretStyle pixelAbove)
        {
            // ReSharper disable once ConvertIfStatementToReturnStatement
            if (pixelAbove == CaretStyle.None) return pixelBelow;

            //todo: low: this is simplified, create matrix of "what's overlaying what"
            return pixelAbove;
        }

        public static bool IsCaret(this CaretStyle caretStyle)
        {
            return caretStyle != CaretStyle.None;
        }

    }
}