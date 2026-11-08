mod hicon;
pub mod queue;

use std::io::BufRead;
use std::path::{Path, PathBuf};

use image::{GenericImageView, RgbaImage};

use queue::{IconExtractor, IconExtractorRequest};
use windows::Win32::Storage::FileSystem::FILE_FLAGS_AND_ATTRIBUTES;
use windows::Win32::UI::Controls::{IImageList, ILD_TRANSPARENT};
use windows::Win32::UI::Shell::{
    SHFILEINFOW, SHGFI_SYSICONINDEX, SHGetFileInfoW, SHGetImageList, SHIL_JUMBO,
};
use windows::Win32::UI::{Shell::ExtractIconExW, WindowsAndMessaging::HICON};

use seelen_core::state::Icon;
use windows_core::Owned;

use crate::{
    error::Result,
    modules::{apps::application::msix::MsixAppsManager, start::application::StartMenuManager},
    resources::RESOURCES,
    utils::{constants::SEELEN_COMMON, date_based_hex_id},
    windows_api::{WindowsApi, string_utils::WindowsString, types::AppUserModelId},
};

pub fn convert_hicon_to_rgba_image(hicon: &HICON) -> Result<RgbaImage> {
    hicon::convert_hicon(hicon).map_err(|error| error.to_string().into())
}

/// this is the best solution having in consideration that a transparent image and have separated pixels
/// with transparent gaps, so search side by side and crop them is the best approach.
pub fn crop_transparent_borders(rgba_image: &RgbaImage) -> RgbaImage {
    let (width, height) = rgba_image.dimensions();
    let mut top = None;
    let mut bottom = None;
    let mut left = None;
    let mut right = None;

    'outer: for y in 0..height {
        for x in 0..width {
            let pixel = rgba_image.get_pixel(x, y);
            if pixel.0[3] != 0 {
                top = Some(y);
                break 'outer;
            }
        }
    }

    let top = match top {
        Some(top) => top,
        None => return RgbaImage::new(1, 1),
    };

    'outer: for y in (top..height).rev() {
        for x in 0..width {
            let pixel = rgba_image.get_pixel(x, y);
            if pixel.0[3] != 0 {
                bottom = Some(y);
                break 'outer;
            }
        }
    }

    let bottom = match bottom {
        Some(bottom) => bottom,
        None => return RgbaImage::new(1, 1),
    };

    'outer: for x in 0..width {
        for y in top..bottom {
            let pixel = rgba_image.get_pixel(x, y);
            if pixel.0[3] != 0 {
                left = Some(x);
                break 'outer;
            }
        }
    }

    let left = match left {
        Some(left) => left,
        None => return RgbaImage::new(1, 1),
    };

    'outer: for x in (left..width).rev() {
        for y in top..bottom {
            let pixel = rgba_image.get_pixel(x, y);
            if pixel.0[3] != 0 {
                right = Some(x);
                break 'outer;
            }
        }
    }

    let right = match right {
        Some(right) => right,
        None => return RgbaImage::new(1, 1),
    };

    rgba_image
        .view(left, top, right - left + 1, bottom - top + 1)
        .to_image()
}

pub fn extract_icon_from_module(path: &Path, index: i32) -> Result<RgbaImage> {
    let path = WindowsString::from(path);
    unsafe {
        let mut hicon = HICON::default();
        let extracted = ExtractIconExW(path.as_pcwstr(), index, Some(&mut hicon), None, 1);
        if extracted == 0 || hicon.is_invalid() {
            return Err(format!("Icon index {index} not found").into());
        }
        let hicon = Owned::new(hicon);

        let image = crop_transparent_borders(&convert_hicon_to_rgba_image(&hicon)?);
        Ok(image)
    }
}

pub fn get_shell_icon(path: &Path) -> Result<RgbaImage> {
    unsafe {
        let normalized = path
            .canonicalize()?
            .to_string_lossy()
            .trim_start_matches(r"\\?\")
            .to_owned();
        let path = WindowsString::from(normalized);

        let mut file_info = SHFILEINFOW::default();
        let result = SHGetFileInfoW(
            path.as_pcwstr(),
            FILE_FLAGS_AND_ATTRIBUTES(0),
            Some(&mut file_info),
            std::mem::size_of::<SHFILEINFOW>() as u32,
            SHGFI_SYSICONINDEX,
        );

        if result == 0 {
            return Err("Failed to get file information".into());
        }

        // file_info.iIcon = 0 is a valid icon but it is the default icon for files on Windows
        // so we will handle this as no icon to avoid generate unnecessary artifacts
        if file_info.iIcon == 0 {
            return Err("Icon index is 0".into());
        }

        let image_list: IImageList = SHGetImageList(SHIL_JUMBO as i32)?;
        // if 256x256 icon is not available, will use the icons with the most color depth and size
        // this is useful for some icons where color depth is less than 32,
        // example: icon of 124x124 16bits and other 64x64 32bits this will return the 32bits icon
        // color depth is prioritized over size
        let icon = Owned::new(image_list.GetIcon(file_info.iIcon, ILD_TRANSPARENT.0)?);
        let image = crop_transparent_borders(&convert_hicon_to_rgba_image(&icon)?);
        Ok(image)
    }
}

fn get_icon_from_url_file(path: &Path) -> Result<RgbaImage> {
    let file = std::fs::File::open(path)?;
    let reader = std::io::BufReader::new(file);

    let mut icon_file = None;
    let mut icon_idx: Option<i32> = None;

    // in theory .url files are encoded in UTF-8 so we don't need to use OsString
    for line in reader.lines() {
        let line = line?;
        if let Some(stripped) = line.strip_prefix("IconFile=") {
            icon_file = Some(PathBuf::from(stripped));
        } else if let Some(stripped) = line.strip_prefix("IconIndex=") {
            icon_idx = Some(stripped.parse()?);
        }
    }

    let icon_file = icon_file.ok_or("Url does not have IconFile")?;
    extract_icon_from_module(&icon_file, icon_idx.unwrap_or(0))
}

/// Parses Windows-style `path,index` icon notation (e.g. `C:\foo\bar.ico,0`).
///
/// Returns `(path, index)` only when the string ends with `,<integer>` **and** the path
/// before the comma points to an existing file.
fn parse_path_with_icon_index(raw: &Path) -> Option<(PathBuf, i32)> {
    let s = raw.to_str()?;
    let comma = s.rfind(',')?;
    let index: i32 = s[comma + 1..].trim().parse().ok()?;
    let path = PathBuf::from(s[..comma].trim());
    if path.exists() && !path.is_dir() {
        Some((path, index))
    } else {
        None
    }
}

/// Extracts an icon from a file at a specific resource index and saves it.
///
/// `key` is the original `path,index` string (e.g. `C:\foo\bar.ico,0`) used as the lookup
/// key in RESOURCES, so different indices of the same file are stored independently.
/// `path` is the actual file path without the index suffix.
fn _extract_and_save_icon_from_module_with_index(
    key: &Path,
    path: &Path,
    index: i32,
) -> Result<()> {
    if RESOURCES.has_app_icon(None, Some(key)) {
        return Ok(());
    }

    let filestem = path.file_stem().ok_or("Failed to get file stem")?;
    let gen_icon_filename = format!(
        "{}_{}.webp",
        filestem.to_string_lossy(),
        date_based_hex_id()
    );

    log::trace!("Extracting icon (index {index}) for {:?}", path.file_name());

    let image = extract_icon_from_module(path, index)?;
    let image = crop_transparent_borders(&image);

    let gen_icon = Icon {
        base: Some(gen_icon_filename.clone()),
        is_aproximately_square: is_aproximately_a_square(&image),
        ..Default::default()
    };

    save_as_webp(
        &image,
        &SEELEN_COMMON
            .system_icon_pack_path()
            .join(&gen_icon_filename),
    )?;
    RESOURCES.add_system_app_icon(None, Some(key), gen_icon);
    Ok(())
}

/// returns the path of the icon extracted from the executable or copied if is an UWP app.
///
/// If the icon already exists, it returns the path instead overriding, this is needed for allow user custom icons.
///
/// umid on this case only applys to Property Store umid
fn _extract_and_save_icon_from_file(origin: &Path) -> Result<()> {
    if !origin.exists() || origin.is_dir() {
        // Handle Windows path,index notation (e.g. "C:\path\app.exe,2" or "file.ico,0")
        if let Some((real_path, index)) = parse_path_with_icon_index(origin) {
            return _extract_and_save_icon_from_module_with_index(origin, &real_path, index);
        }
        return Err(format!("File not found: {}", origin.display()).into());
    }

    let origin_ext = match origin.extension() {
        Some(ext) => ext.to_string_lossy().to_lowercase(),
        // no extension === no icon
        None => return Ok(()),
    };

    // ico files are by itself an icon
    if origin_ext == "ico" {
        return Ok(());
    }

    let is_exe_file = origin_ext == "exe";
    let is_lnk_file = origin_ext == "lnk";
    let is_url_file = origin_ext == "url";

    if is_exe_file || is_lnk_file || is_url_file {
        if RESOURCES.has_app_icon(None, Some(origin)) {
            return Ok(());
        }
    } else if RESOURCES.has_shared_file_icon(origin) {
        return Ok(());
    }

    let file_name = origin.file_name().ok_or("Failed to get file name")?;
    let filestem = origin.file_stem().ok_or("Failed to get file stem")?;

    let gen_icon_filename = format!(
        "{}_{}.webp",
        filestem.to_string_lossy(),
        date_based_hex_id()
    );
    let mut gen_icon = Icon {
        base: Some(gen_icon_filename.clone()),
        ..Default::default()
    };

    log::trace!("Extracting icon for {file_name:?}");

    if origin_ext == "url" {
        let image = get_icon_from_url_file(origin)?;
        gen_icon.is_aproximately_square = is_aproximately_a_square(&image);
        save_as_webp(
            &image,
            &SEELEN_COMMON
                .system_icon_pack_path()
                .join(&gen_icon_filename),
        )?;
        RESOURCES.add_system_app_icon(None, Some(origin), gen_icon);
        return Ok(());
    }

    let mut umid = None;
    if is_lnk_file {
        umid = WindowsApi::get_file_umid(origin).ok();
        let has_custom_icon = WindowsApi::resolve_lnk_custom_icon_path(origin).is_ok();

        if !has_custom_icon {
            let (target, ..) = WindowsApi::resolve_lnk_target(origin)?;
            _extract_and_save_icon_from_file(&target)?;
            RESOURCES.add_system_icon_redirect(umid, origin, &target);
            return Ok(());
        }
    }

    // try get the icon directly from the file
    let icon = get_shell_icon(origin)?;
    gen_icon.is_aproximately_square = is_aproximately_a_square(&icon);

    if is_exe_file || is_lnk_file {
        save_as_webp(
            &icon,
            &SEELEN_COMMON
                .system_icon_pack_path()
                .join(&gen_icon_filename),
        )?;
        RESOURCES.add_system_app_icon(umid.as_deref(), Some(origin), gen_icon);
    } else {
        let gen_icon_filename = format!("{}_{}.webp", origin_ext, date_based_hex_id());
        save_as_webp(
            &icon,
            &SEELEN_COMMON
                .system_icon_pack_path()
                .join(&gen_icon_filename),
        )?;
        gen_icon.base = Some(gen_icon_filename);
        RESOURCES.add_system_file_icon(&origin_ext, gen_icon);
    }

    Ok(())
}

/// returns the path of the icon extracted from the app with the specified package app user model id.
fn _extract_and_save_icon_umid(aumid: &AppUserModelId) -> Result<()> {
    match aumid {
        AppUserModelId::Appx(app_umid) => {
            let msix_manager = MsixAppsManager::instance();
            let path = msix_manager.get_app_path(app_umid)?;
            {
                if RESOURCES.has_app_icon(Some(aumid.as_str()), path.as_deref()) {
                    return Ok(());
                }
            }

            log::trace!("Extracting icon for {app_umid:?}");
            let mut gen_icon = Icon::default();
            let (light_path, dark_path) = msix_manager.get_app_icon_path(app_umid)?;

            let name = date_based_hex_id();

            let light_rgba = image::open(&light_path)?.to_rgba8();
            let light_rgba = crop_transparent_borders(&light_rgba);

            if light_path != dark_path {
                let dark_rgba = image::open(&dark_path)?.to_rgba8();
                let dark_rgba = crop_transparent_borders(&dark_rgba);

                save_as_webp(
                    &light_rgba,
                    &SEELEN_COMMON
                        .system_icon_pack_path()
                        .join(format!("{name}_light.webp")),
                )?;
                save_as_webp(
                    &dark_rgba,
                    &SEELEN_COMMON
                        .system_icon_pack_path()
                        .join(format!("{name}_dark.webp")),
                )?;

                gen_icon.light = Some(format!("{name}_light.webp"));
                gen_icon.dark = Some(format!("{name}_dark.webp"));
            } else {
                save_as_webp(
                    &light_rgba,
                    &SEELEN_COMMON
                        .system_icon_pack_path()
                        .join(format!("{name}.webp")),
                )?;
                gen_icon.base = Some(format!("{name}.webp"));
            }

            gen_icon.is_aproximately_square = is_aproximately_a_square(&light_rgba);

            RESOURCES.add_system_app_icon(Some(app_umid), path.as_deref(), gen_icon);
            Ok(())
        }
        AppUserModelId::PropertyStore(app_umid) => {
            let start = StartMenuManager::instance();
            let lnk = start
                .get_by_file_umid(app_umid)
                .ok_or(format!("No shortcut found for umid {app_umid}"))?;

            {
                if RESOURCES.has_app_icon(Some(aumid.as_str()), Some(&lnk.path)) {
                    return Ok(());
                }
            }

            _extract_and_save_icon_from_file(&lnk.path)?;
            Ok(())
        }
    }
}

pub fn request_icon_extraction_from_file<T: AsRef<Path>>(path: T) {
    let path = path.as_ref();
    if path.as_os_str().is_empty() {
        return;
    }
    IconExtractor::instance().request(IconExtractorRequest::Path(path.to_path_buf()));
}

pub fn request_icon_extraction_from_umid(aumid: &AppUserModelId) {
    IconExtractor::instance().request(IconExtractorRequest::AppUMID(aumid.clone()));
}

const SQUARE_MARGIN: f32 = 0.1;
const ASPECT_TOLERANCE: f32 = 0.05;
const OPACITY_THRESHOLD: u8 = 254;

pub fn is_aproximately_a_square(rgba_image: &RgbaImage) -> bool {
    let (width, height) = rgba_image.dimensions();

    // verify if the image is not empty
    if width == 0 || height == 0 {
        return false;
    }

    // verify if the image is a square
    let aspect_ratio = width as f32 / height as f32;
    if (aspect_ratio - 1.0).abs() > ASPECT_TOLERANCE {
        return false;
    }

    // Calculate margin
    let margin_x = (width as f32 * SQUARE_MARGIN) as u32;
    let margin_y = (height as f32 * SQUARE_MARGIN) as u32;
    let inner_width = width - 2 * margin_x;
    let inner_height = height - 2 * margin_y;

    // verify if the image is a square
    for y in margin_y..margin_y + inner_height {
        for x in margin_x..margin_x + inner_width {
            let pixel = rgba_image.get_pixel(x, y);
            if pixel.0[3] < OPACITY_THRESHOLD {
                return false;
            }
        }
    }

    true
}

fn save_as_webp(image: &RgbaImage, path: &std::path::Path) -> Result<()> {
    let webp_bytes =
        webp::Encoder::from_rgba(image.as_raw(), image.width(), image.height()).encode_lossless();
    std::fs::write(path, &*webp_bytes)?;
    Ok(())
}
