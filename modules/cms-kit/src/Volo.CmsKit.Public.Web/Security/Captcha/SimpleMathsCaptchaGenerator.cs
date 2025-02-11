﻿using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Volo.Abp;
using Volo.CmsKit.Localization;
using Microsoft.Extensions.Localization;
using Volo.Abp.DependencyInjection;
using Color = SixLabors.ImageSharp.Color;
using PointF = SixLabors.ImageSharp.PointF;
using Volo.Abp.Caching;
using Microsoft.Extensions.Caching.Distributed;

namespace Volo.CmsKit.Public.Web.Security.Captcha;

public class SimpleMathsCaptchaGenerator : ITransientDependency
{
    protected IStringLocalizer<CmsKitResource> Localizer { get; }
    protected IDistributedCache<CaptchaOutput> Cache { get; }

    public SimpleMathsCaptchaGenerator(IStringLocalizer<CmsKitResource> localizer, IDistributedCache<CaptchaOutput> cache)
    {
        Localizer = localizer;
        Cache = cache;
    }

    public virtual Task<CaptchaOutput> GenerateAsync()
    {
        return GenerateAsync(options: null, number1: null, number2: null);
    }

    public virtual Task<CaptchaOutput> GenerateAsync(CaptchaOptions options)
    {
        return GenerateAsync(options, number1: null, number2: null);
    }

    /// <summary>
    /// Creates a simple captcha code.
    /// </summary>
    /// <param name="options">Options for captcha generation</param>
    /// <param name="number1">First number for maths operation</param>
    /// <param name="number2">Second number for maths operation</param>
    /// <returns></returns>
    public virtual async Task<CaptchaOutput> GenerateAsync(CaptchaOptions options, int? number1, int? number2)
    {
        var random = new Random();
        options ??= new CaptchaOptions();

        number1 ??= random.Next(options.Number1MinValue, options.Number1MaxValue);
        number2 ??= random.Next(options.Number2MinValue, options.Number2MaxValue);

        var text = number1 + "+" + number2;
        var request = new CaptchaRequest
        {
            Input =
            {
                Number1 = number1.Value,
                Number2 = number2.Value
            },
            Output =
            {
                Text = text,
                Result = Calculate(number1.Value, number2.Value),
                ImageBytes = GenerateInternal(text, options)
            }
        };

        await Cache.SetAsync(request.Output.Id.ToString("N"), request.Output, new DistributedCacheEntryOptions 
        {
            AbsoluteExpiration = DateTimeOffset.Now.Add(options.DurationOfValidity)
        });

        return request.Output;
    }

    private static int Calculate(int number1, int number2)
    {
        return number1 + number2;
    }

    public virtual async Task ValidateAsync(Guid requestId, int value)
    {
        var request = await Cache.GetAsync(requestId.ToString("N"));
        
        if(request == null || request.Result != value) 
        {
            throw new UserFriendlyException(Localizer["CaptchaCodeErrorMessage"]);
        }
    }

    public virtual async Task ValidateAsync(Guid requestId, string value)
    {
        if (int.TryParse(value, out var captchaInput))
        {
            await ValidateAsync(requestId, captchaInput);
        }
        else
        {
            throw new UserFriendlyException(Localizer["CaptchaCodeMissingMessage"]);
        }
    }

    private byte[] GenerateInternal(string stringText, CaptchaOptions options)
    {
        byte[] result;

        using (var image = new Image<Rgba32>(options.Width, options.Height))
        {
            float position = 0;
            var random = new Random();
            var startWith = (byte)random.Next(5, 10);
            image.Mutate(ctx => ctx.BackgroundColor(Color.Transparent));
            var fontFamily = SystemFonts.Families
                .FirstOrDefault(x => x.GetAvailableStyles().Contains(options.FontStyle), SystemFonts.Families.First())
                .Name;
            var font = SystemFonts.CreateFont(fontFamily, options.FontSize, options.FontStyle);
            
            foreach (var character in stringText)
            {
                var text = character.ToString();
                var color = options.TextColor[random.Next(0, options.TextColor.Length)];
                var location = new PointF(startWith + position, random.Next(6, 13));
                image.Mutate(ctx => ctx.DrawText(text, font, color, location));
                position += TextMeasurer.MeasureSize(character.ToString(), new TextOptions(font)).Width;
            }

            //add rotation
            var rotation = GetRotation(options);
            image.Mutate(ctx => ctx.Transform(rotation));

            // add the dynamic image to original image
            var size = (ushort)TextMeasurer.MeasureSize(stringText, new TextOptions(font)).Width;
            var img = new Image<Rgba32>(size + 15, options.Height);
            img.Mutate(ctx => ctx.BackgroundColor(Color.White));

            Parallel.For(0, options.DrawLines, i =>
            {
                var x0 = random.Next(0, random.Next(0, 30));
                var y0 = random.Next(10, img.Height);

                var x1 = random.Next(30, img.Width);
                var y1 = random.Next(0, img.Height);

                img.Mutate(ctx =>
                    ctx.DrawLine(options.TextColor[random.Next(0, options.TextColor.Length)],
                                  RandomTextGenerator.GenerateNextFloat(options.MinLineThickness, options.MaxLineThickness),
                                  new PointF[] { new PointF(x0, y0), new PointF(x1, y1) })
                    );
            });

            img.Mutate(ctx => ctx.DrawImage(image, 0.80f));

            Parallel.For(0, options.NoiseRate, _ =>
            {
                var x0 = random.Next(0, img.Width - 1);
                var y0 = random.Next(0, img.Height - 1);
                img.Mutate(
                        ctx => ctx
                            .DrawLine(options.NoiseRateColor[random.Next(0, options.NoiseRateColor.Length)],
                                RandomTextGenerator.GenerateNextFloat(0.5, 1.5), new (x0, y0), new (x0 + 0.01f, y0 + 0.01f))
                    );
            });

            img.Mutate(x =>
            {
                x.Resize(options.Width, options.Height);
            });

            using (var ms = new MemoryStream())
            {
                img.Save(ms, options.Encoder);
                result = ms.ToArray();
            }
        }

        return result;
    }

    private static AffineTransformBuilder GetRotation(CaptchaOptions options)
    {
        var random = new Random();
        var width = random.Next(10, options.Width);
        var height = random.Next(10, options.Height);
        var pointF = new PointF(width, height);
        var rotationDegrees = random.Next(0, options.MaxRotationDegrees);
        return new AffineTransformBuilder().PrependRotationDegrees(rotationDegrees, pointF);
    }
}