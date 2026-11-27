using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Snappo.Editor;

internal sealed record PixelatedImage(BitmapSource Bitmap, double WidthScale, double HeightScale)
{
    public void Draw(DrawingContext drawing, Rect bounds)
    {
        // The last row or column can be a partial block. Keep the grid aligned with the source pixels.
        var destination = new Rect(bounds.X, bounds.Y, bounds.Width * WidthScale, bounds.Height * HeightScale);
        var blocks = new DrawingGroup();
        RenderOptions.SetBitmapScalingMode(blocks, BitmapScalingMode.NearestNeighbor);
        blocks.Children.Add(new ImageDrawing(Bitmap, destination));
        blocks.Freeze();
        drawing.DrawDrawing(blocks);
    }
}

internal static class ImagePixelator
{
    private const int BytesPerPixel = 4;

    public static PixelatedImage Pixelate(BitmapSource source, int blockSize)
    {
        blockSize = Math.Max(2, blockSize);

        if (source.Format != PixelFormats.Bgr32)
        {
            source = new FormatConvertedBitmap(source, PixelFormats.Bgr32, null, 0);
        }

        int width = source.PixelWidth;
        int height = source.PixelHeight;
        int stride = width * BytesPerPixel;

        int blocksAcross = (width + blockSize - 1) / blockSize;
        int blocksDown = (height + blockSize - 1) / blockSize;
        byte[] blockPixels = new byte[blocksAcross * blocksDown * BytesPerPixel];

        // Read one band of rows at a time instead of copying the whole screenshot into one huge array.
        byte[] band = new byte[stride * Math.Min(blockSize, height)];
        long[] totals = new long[blocksAcross * 3];

        for (int blockRow = 0; blockRow < blocksDown; blockRow++)
        {
            int top = blockRow * blockSize;
            int rows = Math.Min(blockSize, height - top);
            source.CopyPixels(new Int32Rect(0, top, width, rows), band, stride, 0);
            Array.Clear(totals);

            for (int y = 0; y < rows; y++)
            {
                int index = y * stride;
                for (int x = 0; x < width; x++)
                {
                    int total = (x / blockSize) * 3;
                    totals[total] += band[index];
                    totals[total + 1] += band[index + 1];
                    totals[total + 2] += band[index + 2];
                    index += BytesPerPixel;
                }
            }

            for (int blockColumn = 0; blockColumn < blocksAcross; blockColumn++)
            {
                int columns = Math.Min(blockSize, width - blockColumn * blockSize);
                int pixelCount = rows * columns;
                int outputIndex = (blockRow * blocksAcross + blockColumn) * BytesPerPixel;
                int total = blockColumn * 3;

                blockPixels[outputIndex] = (byte)(totals[total] / pixelCount);
                blockPixels[outputIndex + 1] = (byte)(totals[total + 1] / pixelCount);
                blockPixels[outputIndex + 2] = (byte)(totals[total + 2] / pixelCount);
                blockPixels[outputIndex + 3] = 255;
            }
        }

        BitmapSource result = BitmapSource.Create(
            blocksAcross, blocksDown, 96, 96, PixelFormats.Bgr32, null, blockPixels, blocksAcross * BytesPerPixel);
        result.Freeze();
        return new PixelatedImage(result, (double)blocksAcross * blockSize / width, (double)blocksDown * blockSize / height);
    }
}
