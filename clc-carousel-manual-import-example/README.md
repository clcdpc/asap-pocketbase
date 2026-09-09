# CLC Splide Carousel

Accessible Splide-based carousel for displaying catalog items from the carousel service.

## Required dependencies

Include the carousel script. By default, it automatically loads Splide CSS, the carousel CSS, and Splide JavaScript before initializing carousels:

```html
<script src="clc-splide-carousel.js"></script>
```

When deploying updated files, cache-bust the carousel script URL if needed:

```html
<script src="clc-splide-carousel.js?v=20260703-10"></script>
```

When the carousel script URL has a query string, the automatically loaded carousel stylesheet uses the same query string. For example, `clc-splide-carousel.js?v=20260703-10` loads `clc-splide-carousel.css?v=20260703-10` by default.


## Example pages

The package includes two demo pages:

| File | Purpose |
| --- | --- |
| `index.html` | Default one-script setup. The carousel script auto-loads Splide CSS, carousel CSS, and Splide JavaScript. |
| `manual-import.html` | Manual dependency setup. The page imports Splide CSS, carousel CSS, Splide JavaScript, and then disables auto-loading on the carousel script. |

## Basic usage

```html
<section
	class="clc-carousel"
	data-rsid="92105"
	data-branch-id="17"
	data-title="Featured Titles">
</section>
```

The script automatically initializes every `.clc-carousel[data-rsid]` element on the page.

## Data attributes

| Attribute | Required | Default | Description |
| --- | --- | --- | --- |
| `data-rsid` | Yes | none | Carousel record-set ID sent to the service as `rsid`. |
| `data-ctx` | No | `1` | Branch/context value sent to the service as `ctx`. |
| `data-branch-id` | No | `1` | Alias for `data-ctx`. Used only when `data-ctx` is not present. |
| `data-title` | No | none | Visible carousel heading. Also used for the accessible name when present. |
| `data-label` | No | `Catalog records` | Accessible fallback label when there is no visible title. |
| `data-role` | No | `group` | Accessible landmark role. Supported values: `group`, `region`. |
| `data-heading-level` | No | `2` | Heading level for `data-title`. Supported values: `2` through `6`. |
| `data-item-width` | No | `100` | Target cover/item width in pixels. |
| `data-max-items` | No | `6` | Maximum number of visible items before scrolling is needed. The actual visible count may be lower if the containing page is too narrow. |
| `data-show-item-titles` | No | `false` | Shows each item title below its cover. Titles are displayed in a fixed-height area so long titles do not change carousel height or item width. |
| `data-item-title-lines` | No | `2` | Maximum visible title lines when `data-show-item-titles` is true. Supported values are `1` through `4`. Longer titles are clipped inside the fixed title area. |
| `data-arrow-gutter` | No | `50` | Horizontal space reserved for arrows in pixels. Use `0` to remove the reserved gutter. |
| `data-arrow-color` | No | stylesheet default | Normal arrow color. Accepts valid CSS color values, for example `#005ea8`, `rgb(0 94 168)`, or `blue`. |
| `data-arrow-hover-color` | No | `data-arrow-color` when set | Arrow hover color. Accepts valid CSS color values. |
| `data-arrow-disabled-color` | No | `#9a9a9a` | Disabled arrow color. Accepts valid CSS color values. |
| `data-open-new-tab` | No | `false` | Opens catalog links in a new tab when true. |
| `data-endpoint` | No | script origin + `/home/jsonp` | Alternate carousel data endpoint. The script appends `rsid` and `ctx`. Must be `http` or `https`. |
| `data-loading-message` | No | empty | Optional message shown while the carousel loads. |
| `data-empty-message` | No | `No carousel items are currently available.` | Message shown when no usable carousel items remain. |
| `data-error-message` | No | `Carousel items could not be loaded.` | Message shown when the carousel request fails. |

Boolean attributes accept `true`, `1`, or `yes` for true and `false`, `0`, or `no` for false.

## Script attributes

These attributes are set on the `clc-splide-carousel.js` script tag, not on individual carousel sections. A short comment like this is useful in pages that use dependency overrides:

```html
<!--
	Supported script tag attributes:
	- data-auto-load-css: true/false; auto-load missing Splide CSS and carousel CSS.
	- data-auto-load-splide: true/false; auto-load Splide JavaScript when needed.
	- data-splide-src: alternate Splide JavaScript URL.
	- data-splide-css-src: alternate Splide stylesheet URL.
	- data-carousel-css-src: alternate carousel stylesheet URL.
-->
<script src="clc-splide-carousel.js?v=20260703-10"></script>
```

| Attribute | Default | Description |
| --- | --- | --- |
| `data-auto-load-css` | `true` | Automatically injects missing Splide CSS and carousel CSS before initializing. Set to `false` when the page already includes both stylesheets manually. |
| `data-auto-load-splide` | `true` | Automatically injects Splide JavaScript when `window.Splide` is not already present. Set to `false` when the page already includes Splide manually. |
| `data-splide-src` | jsDelivr Splide JS URL | Alternate Splide JavaScript URL. Useful if Splide is hosted locally. |
| `data-splide-css-src` | jsDelivr Splide CSS URL | Alternate Splide stylesheet URL. Useful if Splide CSS is hosted locally. |
| `data-carousel-css-src` | script folder + `clc-splide-carousel.css` | Alternate carousel stylesheet URL. |

### Manual dependency mode

If a page needs explicit control over stylesheet/script loading, include the dependencies manually and disable automatic loading:

```html
<link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/@splidejs/splide@4.1.4/dist/css/splide.min.css">
<link rel="stylesheet" href="clc-splide-carousel.css?v=20260703-10">

<script src="https://cdn.jsdelivr.net/npm/@splidejs/splide@4.1.4/dist/js/splide.min.js"></script>
<script
	src="clc-splide-carousel.js?v=20260703-10"
	data-auto-load-css="false"
	data-auto-load-splide="false">
</script>
```

### Self-hosted Splide URLs

```html
<script
	src="clc-splide-carousel.js?v=20260703-10"
	data-splide-src="/content/splide.min.js"
	data-splide-css-src="/content/splide.min.css">
</script>
```

## Common examples

### Custom arrow color

```html
<section
	class="clc-carousel"
	data-rsid="92105"
	data-branch-id="17"
	data-title="Featured Titles"
	data-arrow-color="#005ea8">
</section>
```

### Custom arrow color and hover color

```html
<section
	class="clc-carousel"
	data-rsid="92105"
	data-branch-id="17"
	data-title="Featured Titles"
	data-arrow-color="#005ea8"
	data-arrow-hover-color="#003f73">
</section>
```

### Missing cover fallback

When the cover endpoint returns a missing-cover image as a 1x1 image, the script automatically hides that image and displays a bounded fallback box in the same 120px cover space.

When item titles are not shown below covers, the fallback box displays the item title. When item titles are shown below covers, the fallback box displays `No cover available` so the title is not duplicated.

No data attribute is required.

### Show item titles below covers

```html
<section
	class="clc-carousel"
	data-rsid="92105"
	data-branch-id="17"
	data-title="Featured Titles"
	data-show-item-titles="true">
</section>
```

By default, visible item titles are clamped to two lines. Long titles stay inside a fixed title area and do not increase the carousel height or item width.

Legacy backslash-escaped punctuation in item titles is normalized before display.

To show a different number of title lines, use `data-item-title-lines`. Values are capped at four lines.

```html
<section
	class="clc-carousel"
	data-rsid="92105"
	data-branch-id="17"
	data-title="Featured Titles"
	data-show-item-titles="true"
	data-item-title-lines="3">
</section>
```

### Open catalog links in a new tab

```html
<section
	class="clc-carousel"
	data-rsid="92105"
	data-branch-id="17"
	data-title="Featured Titles"
	data-open-new-tab="true">
</section>
```

### Alternate endpoint for testing

```html
<section
	class="clc-carousel"
	data-rsid="92105"
	data-branch-id="17"
	data-title="Featured Titles"
	data-endpoint="https://carousels-dev.clcohio.org/home/jsonp">
</section>
```

## JavaScript API

The script exposes a small global API:

```js
window.clcSplideCarousel.version; // "2026.07.03.10"
window.clcSplideCarousel.init();
window.clcSplideCarousel.refresh();
window.clcSplideCarousel.reload();
window.clcSplideCarousel.destroy();
```

Each method also accepts a selector or carousel element when targeting one carousel:

```js
window.clcSplideCarousel.reload("#featured-carousel");
```

| Property / method | Description |
| --- | --- |
| `version` | Current carousel script version string. |
| `init(selectorOrElement)` | Initializes carousels. Automatic on page load, but useful after injecting carousel markup dynamically. Returns a promise because dependencies may need to load first. |
| `refresh(selectorOrElement)` | Recalculates layout using already-loaded items. Returns a promise because dependencies may need to load first. |
| `reload(selectorOrElement)` | Re-fetches carousel data and rebuilds the carousel. Returns a promise because dependencies may need to load first. |
| `destroy(selectorOrElement)` | Destroys one carousel, or all initialized carousels when called without an argument. This stops behavior and requests but intentionally leaves the generated carousel markup in place. |


## Hidden containers

If a carousel is initialized inside hidden content, such as a tab, accordion, or modal, the script defers mounting until it can measure the carousel. A visibility observer handles common cases where the carousel becomes visible later.

For custom show/hide behavior, call `refresh()` after revealing the container:

```js
window.clcSplideCarousel.refresh("#featured-carousel");
```

## Duplicate script loading

The script guards against being initialized more than once. If the same JavaScript file is accidentally included twice, the later copy exits without replacing the existing `window.clcSplideCarousel` API or adding another resize handler.
