//! Candidate for Seelen's shared HICON conversion. No event loop or idle work.
use image::RgbaImage;
use std::{error::Error, mem::size_of};
use windows::Win32::{
    Graphics::Gdi::{
        BI_RGB, BITMAP, BITMAPINFO, BITMAPINFOHEADER, CreateCompatibleDC, DIB_RGB_COLORS, DeleteDC,
        DeleteObject, GetDIBits, GetObjectW, HBITMAP, HDC, RGBQUAD,
    },
    UI::WindowsAndMessaging::{GetIconInfoExW, HICON, ICONINFOEXW},
};

type Result<T> = std::result::Result<T, Box<dyn Error>>;
const MAX_SIDE: i32 = 4096;

struct Bitmap(HBITMAP);
impl Drop for Bitmap {
    fn drop(&mut self) {
        if !self.0.is_invalid() {
            unsafe {
                let _ = DeleteObject(self.0.into());
            }
        }
    }
}
struct Dc(HDC);
impl Drop for Dc {
    fn drop(&mut self) {
        unsafe {
            let _ = DeleteDC(self.0);
        }
    }
}

#[repr(C)]
struct MaskInfo {
    header: BITMAPINFOHEADER,
    colors: [RGBQUAD; 2],
}

fn dimensions(bitmap: HBITMAP) -> Result<(i32, i32)> {
    let mut value = BITMAP::default();
    if unsafe {
        GetObjectW(
            bitmap.into(),
            size_of::<BITMAP>() as i32,
            Some((&mut value as *mut BITMAP).cast()),
        )
    } == 0
    {
        return Err("GetObjectW failed".into());
    }
    let height = value
        .bmHeight
        .checked_abs()
        .ok_or("Invalid bitmap height")?;
    if !(1..=MAX_SIDE).contains(&value.bmWidth) || !(1..=MAX_SIDE * 2).contains(&height) {
        return Err("Unexpected icon dimensions".into());
    }
    Ok((value.bmWidth, height))
}

fn read_color(dc: HDC, bitmap: HBITMAP, width: i32, height: i32) -> Result<Vec<u8>> {
    if height > MAX_SIDE {
        return Err("Unexpected color icon height".into());
    }
    let mut info = BITMAPINFO {
        bmiHeader: BITMAPINFOHEADER {
            biSize: size_of::<BITMAPINFOHEADER>() as u32,
            biWidth: width,
            biHeight: -height,
            biPlanes: 1,
            biBitCount: 32,
            biCompression: BI_RGB.0,
            ..Default::default()
        },
        ..Default::default()
    };
    let mut bytes = vec![0; width as usize * height as usize * 4];
    // GetDIBits requires that the source bitmap is NOT selected into any DC.
    let lines = unsafe {
        GetDIBits(
            dc,
            bitmap,
            0,
            height as u32,
            Some(bytes.as_mut_ptr().cast()),
            &mut info,
            DIB_RGB_COLORS,
        )
    };
    if lines != height {
        return Err("Incomplete color bitmap read".into());
    }
    Ok(bytes)
}

fn read_mask(dc: HDC, bitmap: HBITMAP, width: i32, height: i32) -> Result<(Vec<u8>, usize)> {
    let mut info = MaskInfo {
        header: BITMAPINFOHEADER {
            biSize: size_of::<BITMAPINFOHEADER>() as u32,
            biWidth: width,
            biHeight: -height,
            biPlanes: 1,
            biBitCount: 1,
            biCompression: BI_RGB.0,
            biClrUsed: 2,
            ..Default::default()
        },
        colors: [
            RGBQUAD::default(),
            RGBQUAD {
                rgbBlue: 255,
                rgbGreen: 255,
                rgbRed: 255,
                rgbReserved: 0,
            },
        ],
    };
    let stride = (width as usize).div_ceil(32) * 4;
    let mut bytes = vec![0; stride * height as usize];
    let lines = unsafe {
        GetDIBits(
            dc,
            bitmap,
            0,
            height as u32,
            Some(bytes.as_mut_ptr().cast()),
            (&mut info as *mut MaskInfo).cast::<BITMAPINFO>(),
            DIB_RGB_COLORS,
        )
    };
    if lines != height {
        return Err("Incomplete icon mask read".into());
    }
    Ok((bytes, stride))
}

fn bit(bytes: &[u8], stride: usize, x: usize, y: usize) -> bool {
    bytes[y * stride + x / 8] & (0x80 >> (x % 8)) != 0
}

pub fn convert_hicon(hicon: &HICON) -> Result<RgbaImage> {
    let mut info = ICONINFOEXW {
        cbSize: size_of::<ICONINFOEXW>() as u32,
        ..Default::default()
    };
    if !unsafe { GetIconInfoExW(*hicon, &mut info) }.as_bool() {
        return Err("GetIconInfoExW failed".into());
    }
    let mask = Bitmap(info.hbmMask);
    let color = Bitmap(info.hbmColor);
    let dc = Dc(unsafe { CreateCompatibleDC(None) });
    if dc.0.is_invalid() {
        return Err("CreateCompatibleDC failed".into());
    }

    if !color.0.is_invalid() {
        let (width, height) = dimensions(color.0)?;
        let mut bytes = read_color(dc.0, color.0, width, height)?;
        let has_alpha = bytes.chunks_exact(4).any(|pixel| pixel[3] != 0);
        // Preserve the color values returned by GetDIBits for an alpha HICON.
        // Applying inverse premultiplication here fails native DrawIconEx comparisons.
        // AND masks are authoritative only for legacy images with no alpha values.
        if !has_alpha {
            if mask.0.is_invalid() {
                return Err("Legacy icon has no transparency mask".into());
            }
            let dimensions = dimensions(mask.0)?;
            if dimensions != (width, height) {
                return Err("Color/mask dimensions disagree".into());
            }
            let (bits, stride) = read_mask(dc.0, mask.0, width, height)?;
            for y in 0..height as usize {
                for x in 0..width as usize {
                    bytes[(y * width as usize + x) * 4 + 3] =
                        if bit(&bits, stride, x, y) { 0 } else { 255 };
                }
            }
        }
        for pixel in bytes.chunks_exact_mut(4) {
            pixel.swap(0, 2);
        }
        return RgbaImage::from_raw(width as u32, height as u32, bytes)
            .ok_or_else(|| "Image buffer mismatch".into());
    }

    if mask.0.is_invalid() {
        return Err("Icon has neither color nor mask".into());
    }
    let (width, doubled_height) = dimensions(mask.0)?;
    if doubled_height % 2 != 0 {
        return Err("Malformed monochrome mask".into());
    }
    let height = doubled_height / 2;
    let (bits, stride) = read_mask(dc.0, mask.0, width, doubled_height)?;
    let mut pixels = vec![0; width as usize * height as usize * 4];
    for y in 0..height as usize {
        for x in 0..width as usize {
            let and = bit(&bits, stride, x, y);
            let xor = bit(&bits, stride, x, y + height as usize);
            // Destination-dependent inversion cannot be encoded faithfully as RGBA.
            if and && xor {
                return Err("Monochrome inversion requires destination composition".into());
            }
            let pixel = &mut pixels[(y * width as usize + x) * 4..][..4];
            let value = if xor { 255 } else { 0 };
            pixel.copy_from_slice(&[value, value, value, if and { 0 } else { 255 }]);
        }
    }
    RgbaImage::from_raw(width as u32, height as u32, pixels)
        .ok_or_else(|| "Image buffer mismatch".into())
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::ffi::c_void;
    use windows::Win32::{
        Graphics::Gdi::{CreateDIBSection, SelectObject},
        UI::WindowsAndMessaging::{
            CopyIcon, CreateIconIndirect, DI_NORMAL, DestroyIcon, DrawIconEx, ICONINFO,
            IDI_APPLICATION, IDI_INFORMATION, IDI_WARNING, LoadIconW,
        },
    };
    struct Icon(HICON);
    impl Drop for Icon {
        fn drop(&mut self) {
            unsafe {
                let _ = DestroyIcon(self.0);
            }
        }
    }

    fn dib(width: i32, height: i32, color: bool, source: &[u8]) -> Bitmap {
        let mut info = MaskInfo {
            header: BITMAPINFOHEADER {
                biSize: size_of::<BITMAPINFOHEADER>() as u32,
                biWidth: width,
                biHeight: -height,
                biPlanes: 1,
                biBitCount: if color { 32 } else { 1 },
                biCompression: BI_RGB.0,
                biClrUsed: if color { 0 } else { 2 },
                ..Default::default()
            },
            colors: [
                RGBQUAD::default(),
                RGBQUAD {
                    rgbBlue: 255,
                    rgbGreen: 255,
                    rgbRed: 255,
                    rgbReserved: 0,
                },
            ],
        };
        let mut bits = std::ptr::null_mut::<c_void>();
        let bitmap = unsafe {
            CreateDIBSection(
                None,
                (&mut info as *mut MaskInfo).cast(),
                DIB_RGB_COLORS,
                &mut bits,
                None,
                0,
            )
        }
        .unwrap();
        unsafe {
            std::ptr::copy_nonoverlapping(source.as_ptr(), bits.cast(), source.len());
        }
        Bitmap(bitmap)
    }
    fn icon(width: i32, height: i32, color: Option<&[u8]>, mask: &[u8]) -> Icon {
        let mask = dib(
            width,
            if color.is_some() { height } else { height * 2 },
            false,
            mask,
        );
        let color = color.map(|bytes| dib(width, height, true, bytes));
        let info = ICONINFO {
            fIcon: true.into(),
            hbmMask: mask.0,
            hbmColor: color.as_ref().map(|v| v.0).unwrap_or_default(),
            ..Default::default()
        };
        Icon(unsafe { CreateIconIndirect(&info) }.unwrap())
    }
    fn native_rgb(icon: &Icon, width: i32, height: i32, background: u8) -> Vec<u8> {
        let bytes = vec![background; width as usize * height as usize * 4];
        let bitmap = dib(width, height, true, &bytes);
        let dc = Dc(unsafe { CreateCompatibleDC(None) });
        let old = unsafe { SelectObject(dc.0, bitmap.0.into()) };
        unsafe { DrawIconEx(dc.0, 0, 0, icon.0, width, height, 0, None, DI_NORMAL) }.unwrap();
        unsafe {
            SelectObject(dc.0, old);
        }
        read_color(dc.0, bitmap.0, width, height).unwrap()
    }
    fn matches_native(icon: &Icon, width: i32, height: i32) {
        let rgba = convert_hicon(&icon.0).unwrap();
        for background in [0, 91, 255] {
            let reference = native_rgb(icon, width, height, background);
            for (pixel, bgra) in rgba.pixels().zip(reference.chunks_exact(4)) {
                let alpha = pixel[3] as i32;
                for (channel, native) in [pixel[2], pixel[1], pixel[0]].into_iter().zip(&bgra[..3])
                {
                    let actual = (channel as i32 * alpha + background as i32 * (255 - alpha)) / 255;
                    assert!(
                        (actual - *native as i32).abs() <= 1,
                        "native mismatch: {actual} vs {native}"
                    );
                }
            }
        }
    }
    #[test]
    fn transparent_legacy_blink_frame_uses_and_mask() {
        let bytes = vec![0; 16 * 16 * 4];
        let mask = vec![255; 4 * 16];
        let icon = icon(16, 16, Some(&bytes), &mask);
        let image = convert_hicon(&icon.0).unwrap();
        assert!(image.pixels().all(|p| p[3] == 0));
        matches_native(&icon, 16, 16);
    }
    #[test]
    fn opaque_black_is_not_erased() {
        let bytes = vec![0; 16 * 16 * 4];
        let mask = vec![0; 4 * 16];
        let icon = icon(16, 16, Some(&bytes), &mask);
        let image = convert_hicon(&icon.0).unwrap();
        assert!(image.pixels().all(|p| p.0 == [0, 0, 0, 255]));
        matches_native(&icon, 16, 16);
    }
    #[test]
    fn legacy_mixed_mask_matches_native_backgrounds() {
        let mut bytes = vec![0; 16 * 16 * 4];
        for y in 0..16 {
            for x in 0..8 {
                bytes[(y * 16 + x) * 4 + 2] = 255;
            }
        }
        let mut mask = vec![0; 4 * 16];
        for y in 0..16 {
            mask[y * 4 + 1] = 255;
        }
        let icon = icon(16, 16, Some(&bytes), &mask);
        matches_native(&icon, 16, 16);
    }
    #[test]
    fn alpha_icon_preserves_alpha_and_ignores_mask() {
        let bytes = [32, 64, 96, 128].repeat(16 * 16);
        let mask = vec![255; 4 * 16];
        let icon = icon(16, 16, Some(&bytes), &mask);
        let image = convert_hicon(&icon.0).unwrap();
        assert!(image.pixels().all(|p| p[3] == 128));
        matches_native(&icon, 16, 16);
    }
    #[test]
    fn odd_dimensions_do_not_skip_bgra_tail_pixels() {
        let bytes = [2, 7, 11, 255].repeat(13 * 3);
        let icon = icon(13, 3, Some(&bytes), &vec![0; 4 * 3]);
        let image = convert_hicon(&icon.0).unwrap();
        assert!(image.pixels().all(|p| p.0 == [11, 7, 2, 255]));
        matches_native(&icon, 13, 3);
    }
    #[test]
    fn transparent_monochrome_frame_is_supported() {
        let mut mask = vec![0; 4 * 16 * 2];
        mask[..4 * 16].fill(255);
        let icon = icon(16, 16, None, &mask);
        let image = convert_hicon(&icon.0).unwrap();
        assert!(image.pixels().all(|p| p[3] == 0));
        matches_native(&icon, 16, 16);
    }
    #[test]
    fn invalid_icon_returns_error() {
        assert!(convert_hicon(&HICON::default()).is_err());
    }
    #[test]
    fn system_icons_match_native_draw_icon() {
        for id in [IDI_APPLICATION, IDI_INFORMATION, IDI_WARNING] {
            let shared = unsafe { LoadIconW(None, id) }.unwrap();
            let copied = Icon(unsafe { CopyIcon(shared) }.unwrap());
            let image = convert_hicon(&copied.0).unwrap();
            matches_native(&copied, image.width() as i32, image.height() as i32);
        }
    }
    #[test]
    fn several_alpha_levels_match_native_draw_icon() {
        for alpha in [1, 2, 32, 96, 128, 192, 254] {
            let bytes = [alpha / 8, alpha / 4, alpha / 2, alpha].repeat(16 * 16);
            let copied = icon(16, 16, Some(&bytes), &vec![255; 4 * 16]);
            matches_native(&copied, 16, 16);
        }
    }
    #[test]
    fn repeated_conversions_release_gdi_objects() {
        use windows::Win32::System::Threading::{
            GR_GDIOBJECTS, GetCurrentProcess, GetGuiResources,
        };
        let bytes = vec![0; 16 * 16 * 4];
        let opaque = icon(16, 16, Some(&bytes), &vec![0; 4 * 16]);
        let transparent = icon(16, 16, Some(&bytes), &vec![255; 4 * 16]);
        let mut inversion_mask = vec![0; 4 * 16 * 2];
        inversion_mask.fill(255);
        let unsupported = icon(16, 16, None, &inversion_mask);
        // Warm up the GDI path before counting. Run this suite with one test thread.
        convert_hicon(&opaque.0).unwrap();
        let process = unsafe { GetCurrentProcess() };
        let before = unsafe { GetGuiResources(process, GR_GDIOBJECTS) };
        assert!(before > 0, "GDI object count is unavailable");
        let start = std::time::Instant::now();
        for _ in 0..1000 {
            convert_hicon(&opaque.0).unwrap();
            convert_hicon(&transparent.0).unwrap();
            assert!(convert_hicon(&unsupported.0).is_err());
        }
        let after = unsafe { GetGuiResources(process, GR_GDIOBJECTS) };
        eprintln!(
            "GDI objects before={before} after={after}; 3000 conversions elapsed={:?}",
            start.elapsed()
        );
        assert_eq!(before, after, "HICON conversion leaked GDI resources");
    }
}
