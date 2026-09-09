(function () {
	if (window.clcSplideCarousel && typeof window.clcSplideCarousel.init === "function") {
		return;
	}

	/*
		Supported data attributes on this script tag:
		- data-auto-load-css: true/false; auto-load missing Splide CSS and carousel CSS.
		- data-auto-load-splide: true/false; auto-load Splide JavaScript when needed.
		- data-splide-src: alternate Splide JavaScript URL.
		- data-splide-css-src: alternate Splide stylesheet URL.
		- data-carousel-css-src: alternate carousel stylesheet URL.
	*/
	const currentScript = document.currentScript || document.querySelector("script[src*='clc-splide-carousel']");
	const scriptOrigin = getScriptOrigin(currentScript);
	const defaultEndpoint = scriptOrigin + "/home/jsonp";
	const defaultSplideScriptUrl = "https://cdn.jsdelivr.net/npm/@splidejs/splide@4.1.4/dist/js/splide.min.js";
	const defaultSplideCssUrl = "https://cdn.jsdelivr.net/npm/@splidejs/splide@4.1.4/dist/css/splide.min.css";
	const splideInstances = new Map();
	const resizeObservers = new Map();
	const visibilityObservers = new Map();
	const resizeTimers = new WeakMap();
	const requestControllers = new WeakMap();
	const renderTokens = new WeakMap();
	const defaultArrowDisabledColor = "#9a9a9a";
	const stylesheetLoadPromises = new Map();
	let carouselIndex = 0;
	let renderTokenIndex = 0;
	let windowResizeTimer = null;
	let dependencyLoadPromise = null;
	let splideLoadPromise = null;

	window.clcSplideCarousel = {
		version: "2026.07.03.10",
		init: initCarousels,
		refresh: refreshCarousels,
		reload: reloadCarousels,
		destroy: destroyCarousel
	};

	function getScriptOrigin(script) {
		if (script && script.src) {
			try {
				const url = new URL(script.src, window.location.href);

				if (url.protocol === "http:" || url.protocol === "https:") {
					return url.origin;
				}
			} catch (error) {
				console.warn("Unable to determine carousel script origin.", error);
			}
		}

		return "https://carousels.clcohio.org";
	}

	function ensureCarouselDependenciesLoaded() {
		if (dependencyLoadPromise) {
			return dependencyLoadPromise;
		}

		dependencyLoadPromise = ensureCarouselStylesLoaded()
			.then(ensureSplideScriptLoaded)
			.catch(function (error) {
				dependencyLoadPromise = null;
				throw error;
			});

		return dependencyLoadPromise;
	}

	function ensureCarouselStylesLoaded() {
		if (!getBooleanScriptAttribute("data-auto-load-css", true)) {
			return Promise.resolve();
		}

		return Promise.all([
			loadStylesheetIfNeeded(getSplideCssUrl(), "data-clc-splide-css", isSplideCssLink),
			loadStylesheetIfNeeded(getCarouselCssUrl(), "data-clc-carousel-css", isCarouselCssLink)
		]).then(function () {});
	}

	function ensureSplideScriptLoaded() {
		if (window.Splide && typeof window.Splide === "function") {
			return Promise.resolve();
		}

		if (!getBooleanScriptAttribute("data-auto-load-splide", true)) {
			return Promise.reject(new Error("Splide is not available and automatic Splide loading is disabled."));
		}

		if (splideLoadPromise) {
			return splideLoadPromise;
		}

		splideLoadPromise = new Promise(function (resolve, reject) {
			const script = document.createElement("script");

			script.src = getSplideScriptUrl();
			script.async = true;
			script.setAttribute("data-clc-splide-script", "true");

			script.onload = function () {
				if (window.Splide && typeof window.Splide === "function") {
					resolve();
					return;
				}

				reject(new Error("Splide loaded, but window.Splide was not available."));
			};

			script.onerror = function () {
				reject(new Error("Splide could not be loaded from " + script.src + "."));
			};

			document.head.appendChild(script);
		}).catch(function (error) {
			splideLoadPromise = null;
			throw error;
		});

		return splideLoadPromise;
	}

	function loadStylesheetIfNeeded(href, markerAttribute, linkMatcher) {
		const normalizedHref = getSafeHttpUrl(href, window.location.href) || href;
		const existingLink = findExistingStylesheet(normalizedHref, markerAttribute, linkMatcher);

		if (existingLink) {
			existingLink.setAttribute(markerAttribute, "true");
			return Promise.resolve();
		}

		if (stylesheetLoadPromises.has(normalizedHref)) {
			return stylesheetLoadPromises.get(normalizedHref);
		}

		const loadPromise = new Promise(function (resolve, reject) {
			const link = document.createElement("link");

			link.rel = "stylesheet";
			link.href = normalizedHref;
			link.setAttribute(markerAttribute, "true");

			link.onload = function () {
				resolve();
			};

			link.onerror = function () {
				reject(new Error("Stylesheet could not be loaded from " + link.href + "."));
			};

			document.head.appendChild(link);
		}).catch(function (error) {
			stylesheetLoadPromises.delete(normalizedHref);
			throw error;
		});

		stylesheetLoadPromises.set(normalizedHref, loadPromise);
		return loadPromise;
	}

	function findExistingStylesheet(href, markerAttribute, linkMatcher) {
		const links = Array.prototype.slice.call(document.querySelectorAll("link[rel~='stylesheet']"));

		for (let i = 0; i < links.length; i++) {
			const link = links[i];

			if (link.getAttribute(markerAttribute) === "true") {
				return link;
			}

			if (link.href === href) {
				return link;
			}

			if (typeof linkMatcher === "function" && linkMatcher(link)) {
				return link;
			}
		}

		return null;
	}

	function isSplideCssLink(link) {
		const href = (link.getAttribute("href") || "").toLowerCase();
		return href.indexOf("@splidejs/splide") >= 0 || href.indexOf("splide.min.css") >= 0 || href.indexOf("splide-core.min.css") >= 0;
	}

	function isCarouselCssLink(link) {
		const href = (link.getAttribute("href") || "").toLowerCase();
		return href.indexOf("clc-splide-carousel.css") >= 0;
	}

	function getSplideScriptUrl() {
		return getConfiguredDependencyUrl("data-splide-src", defaultSplideScriptUrl, false);
	}

	function getSplideCssUrl() {
		return getConfiguredDependencyUrl("data-splide-css-src", defaultSplideCssUrl, false);
	}

	function getCarouselCssUrl() {
		return getConfiguredDependencyUrl("data-carousel-css-src", getScriptRelativeUrl("clc-splide-carousel.css", true), true);
	}

	function getConfiguredDependencyUrl(attributeName, fallbackUrl, allowRelative) {
		const configuredValue = currentScript ? currentScript.getAttribute(attributeName) : "";
		const candidate = configuredValue && configuredValue.trim() ? configuredValue.trim() : fallbackUrl;

		try {
			const baseUrl = currentScript && currentScript.src ? currentScript.src : window.location.href;
			const url = new URL(candidate, baseUrl);

			if (url.protocol === "http:" || url.protocol === "https:" || (allowRelative && url.protocol === "file:")) {
				return url.toString();
			}
		} catch (error) {
			console.warn("Carousel ignored invalid dependency URL in " + attributeName + ".", error);
		}

		return fallbackUrl;
	}

	function getScriptRelativeUrl(fileName, copyScriptQuery) {
		if (currentScript && currentScript.src) {
			try {
				const scriptUrl = new URL(currentScript.src, window.location.href);
				const dependencyUrl = new URL(fileName, scriptUrl);

				if (copyScriptQuery && scriptUrl.search && dependencyUrl.protocol !== "file:") {
					dependencyUrl.search = scriptUrl.search;
				}

				return dependencyUrl.toString();
			} catch (error) {
				console.warn("Unable to determine carousel dependency URL.", error);
			}
		}

		return scriptOrigin + "/content/" + fileName;
	}

	function getBooleanScriptAttribute(attributeName, fallback) {
		if (!currentScript) {
			return fallback;
		}

		return getBooleanDataAttribute(currentScript, attributeName, fallback);
	}

	function handleDependencyLoadError(selectorOrElement, error) {
		console.error("Unable to load carousel dependencies.", error);
		showDependencyErrorCarousels(selectorOrElement);
	}

	function showDependencyErrorCarousels(selectorOrElement) {
		getCarouselElements(selectorOrElement).forEach(function (carousel) {
			if (!carousel.getAttribute("data-rsid")) {
				return;
			}

			if (carousel.getAttribute("data-clc-carousel-initialized") !== "true") {
				const title = carousel.getAttribute("data-title") || "";
				const label = carousel.getAttribute("data-label") || "Catalog records";

				carousel.setAttribute("data-clc-carousel-initialized", "true");

				if (!carousel.id) {
					carousel.id = "clc-carousel-" + (++carouselIndex);
				}

				applyCarouselStyleOptions(carousel);
				buildCarouselScaffolding(carousel, title, label);
			}

			showCarouselMessage(carousel, getErrorMessage(carousel));
		});
	}

	function initCarousels(selectorOrElement) {
		return ensureCarouselDependenciesLoaded()
			.then(function () {
				initCarouselsAfterDependencies(selectorOrElement);
			})
			.catch(function (error) {
				handleDependencyLoadError(selectorOrElement, error);
			});
	}

	function initCarouselsAfterDependencies(selectorOrElement) {
		getCarouselElements(selectorOrElement).forEach(function (carousel) {
			initializeCarouselElement(carousel);
		});
	}

	function initializeCarouselElement(carousel) {
		const title = carousel.getAttribute("data-title") || "";
		const label = carousel.getAttribute("data-label") || "Catalog records";

		if (!carousel.getAttribute("data-rsid")) {
			console.warn("Carousel is missing data-rsid.", carousel);
			return;
		}

		if (carousel.getAttribute("data-clc-carousel-initialized") === "true") {
			return;
		}

		carousel.setAttribute("data-clc-carousel-initialized", "true");

		if (!carousel.id) {
			carousel.id = "clc-carousel-" + (++carouselIndex);
		}

		applyCarouselStyleOptions(carousel);
		buildCarouselScaffolding(carousel, title, label);
		observeCarouselSize(carousel);
		observeCarouselVisibility(carousel);
		loadCarouselData(carousel);
	}

	function applyCarouselStyleOptions(carousel) {
		const colors = getCarouselArrowColors(carousel);

		carousel.clcCarouselArrowColors = colors;
		carousel.style.removeProperty("--clc-carousel-arrow-color");
		carousel.style.removeProperty("--clc-carousel-arrow-hover-color");
		carousel.style.removeProperty("--clc-carousel-arrow-disabled-color");

		if (colors.color) {
			carousel.style.setProperty("--clc-carousel-arrow-color", colors.color);
		}

		if (colors.hoverColor) {
			carousel.style.setProperty("--clc-carousel-arrow-hover-color", colors.hoverColor);
		}

		if (colors.disabledColor) {
			carousel.style.setProperty("--clc-carousel-arrow-disabled-color", colors.disabledColor);
		}

		applyRenderedArrowColors(carousel, colors);
	}

	function getCarouselArrowColors(carousel) {
		const arrowColor = getCssColorAttribute(carousel, "data-arrow-color");
		const arrowHoverColor = getCssColorAttribute(carousel, "data-arrow-hover-color");
		const arrowDisabledColor = getCssColorAttribute(carousel, "data-arrow-disabled-color");

		return {
			color: arrowColor,
			hoverColor: arrowHoverColor || arrowColor,
			disabledColor: arrowDisabledColor
		};
	}

	function applyRenderedArrowColors(carousel, colors) {
		const arrowColors = colors || carousel.clcCarouselArrowColors || getCarouselArrowColors(carousel);

		const arrows = carousel.querySelectorAll(".splide__arrow");

		arrows.forEach(function (arrow) {
			const disabledColor = arrowColors.disabledColor || defaultArrowDisabledColor;
			const displayColor = arrow.disabled ? disabledColor : arrowColors.color;

			if (arrowColors.color) {
				arrow.style.setProperty("--clc-carousel-arrow-color", arrowColors.color);
			}

			if (arrowColors.hoverColor) {
				arrow.style.setProperty("--clc-carousel-arrow-hover-color", arrowColors.hoverColor);
			}

			arrow.style.setProperty("--clc-carousel-arrow-disabled-color", disabledColor);

			if (displayColor) {
				setArrowElementColors(arrow, displayColor);
			}

			attachArrowColorEvents(carousel, arrow);
		});
	}

	function syncCarouselArrowState(carousel, splide, layout) {
		if (!splide || !layout || !layout.canSlide) {
			return;
		}

		const arrows = getCarouselArrowElements(carousel);

		if (!arrows.prev || !arrows.next) {
			return;
		}

		const itemCount = carousel.clcCarouselItems ? carousel.clcCarouselItems.length : 0;
		const maxStartIndex = Math.max(0, itemCount - layout.perPage);
		const currentIndex = Math.max(0, Math.min(Number(splide.index) || 0, maxStartIndex));

		setCarouselArrowDisabled(arrows.prev, currentIndex <= 0);
		setCarouselArrowDisabled(arrows.next, currentIndex >= maxStartIndex);
		applyRenderedArrowColors(carousel);
	}

	function getCarouselArrowElements(carousel) {
		return {
			prev: carousel.querySelector(".splide__arrow--prev"),
			next: carousel.querySelector(".splide__arrow--next")
		};
	}

	function setCarouselArrowDisabled(arrow, disabled) {
		if (!arrow) {
			return;
		}

		arrow.disabled = disabled;
		arrow.setAttribute("aria-disabled", disabled ? "true" : "false");
		arrow.classList.toggle("clc-carousel-arrow-disabled", disabled);
	}

	function attachArrowColorEvents(carousel, arrow) {
		if (arrow.clcCarouselArrowColorEventsAttached) {
			return;
		}

		arrow.clcCarouselArrowColorEventsAttached = true;

		arrow.addEventListener("mouseenter", function () {
			if (arrow.disabled) {
				return;
			}

			const colors = carousel.clcCarouselArrowColors || getCarouselArrowColors(carousel);
			setArrowElementColors(arrow, colors.hoverColor || colors.color);
		});

		arrow.addEventListener("mouseleave", function () {
			const colors = carousel.clcCarouselArrowColors || getCarouselArrowColors(carousel);
			const disabledColor = colors.disabledColor || defaultArrowDisabledColor;
			setArrowElementColors(arrow, arrow.disabled ? disabledColor : colors.color);
		});
	}

	function setArrowElementColors(arrow, color) {
		if (!color) {
			return;
		}

		setElementColor(arrow, color);

		arrow.querySelectorAll("svg, path, use").forEach(function (icon) {
			setElementColor(icon, color);
		});
	}

	function setElementColor(element, color) {
		element.style.setProperty("color", color, "important");
		element.style.setProperty("fill", color, "important");
		element.style.setProperty("stroke", color, "important");
	}

	function getCssColorAttribute(element, attributeName) {
		const value = element.getAttribute(attributeName);

		if (!value) {
			return "";
		}

		const color = value.trim();

		if (!color) {
			return "";
		}

		if (window.CSS && typeof window.CSS.supports === "function" && !window.CSS.supports("color", color)) {
			console.warn("Carousel ignored invalid color in " + attributeName + ".", element);
			return "";
		}

		return color;
	}

	function buildCarouselScaffolding(carousel, title, label) {
		const accessibleName = title || label || "Catalog records";
		const role = getCarouselRole(carousel);
		const headingLevel = getHeadingLevel(carousel);
		const titleId = carousel.id + "-title";
		const titleHtml = title
			? '<h' + headingLevel + ' id="' + escapeAttribute(titleId) + '" class="clc-carousel-title">' + escapeHtml(title) + '</h' + headingLevel + '>'
			: "";

		carousel.classList.add("splide");
		carousel.setAttribute("role", role);
		carousel.setAttribute("aria-roledescription", "carousel");

		if (title) {
			carousel.setAttribute("aria-labelledby", titleId);
			carousel.removeAttribute("aria-label");
		} else {
			carousel.setAttribute("aria-label", accessibleName);
			carousel.removeAttribute("aria-labelledby");
		}

		carousel.innerHTML =
			titleHtml +
			'<div class="clc-carousel-shell">' +
				'<div class="splide__arrows">' +
					'<button class="splide__arrow splide__arrow--prev" type="button" aria-label="' + escapeAttribute("Show previous " + accessibleName) + '"></button>' +
					'<button class="splide__arrow splide__arrow--next" type="button" aria-label="' + escapeAttribute("Show next " + accessibleName) + '"></button>' +
				'</div>' +
				'<div class="clc-carousel-frame">' +
					'<div class="splide__track">' +
						'<ul class="splide__list carousel-container"></ul>' +
					'</div>' +
				'</div>' +
			'</div>' +
			'<div class="clc-carousel-message" role="status" hidden></div>';
	}

	function getCarouselRole(carousel) {
		const role = (carousel.getAttribute("data-role") || "group").toLowerCase();

		if (role === "region" || role === "group") {
			return role;
		}

		return "group";
	}

	function getHeadingLevel(carousel) {
		const level = parseInt(carousel.getAttribute("data-heading-level"), 10);

		if (Number.isFinite(level) && level >= 2 && level <= 6) {
			return level;
		}

		return 2;
	}

	function getCarouselRequestUrl(carousel) {
		const rsid = carousel.getAttribute("data-rsid");

		if (!rsid) {
			return "";
		}

		const ctx = carousel.getAttribute("data-ctx") || carousel.getAttribute("data-branch-id") || "1";
		const url = new URL(getCarouselEndpoint(carousel), window.location.href);

		url.searchParams.set("rsid", rsid);
		url.searchParams.set("ctx", ctx);

		return url.toString();
	}

	function getCarouselEndpoint(carousel) {
		const endpoint = carousel.getAttribute("data-endpoint");

		if (!endpoint || !endpoint.trim()) {
			return defaultEndpoint;
		}

		try {
			const url = new URL(endpoint.trim(), window.location.href);

			if (url.protocol === "http:" || url.protocol === "https:") {
				return url.toString();
			}
		} catch (error) {
			console.warn("Carousel ignored invalid data-endpoint.", error, carousel);
		}

		console.warn("Carousel ignored invalid data-endpoint.", carousel);
		return defaultEndpoint;
	}

	function loadCarouselData(carousel) {
		const requestUrl = getCarouselRequestUrl(carousel);

		if (!requestUrl) {
			showCarouselMessage(carousel, getErrorMessage(carousel));
			return;
		}

		abortCarouselRequest(carousel);
		invalidateCarouselRender(carousel);
		carousel.clcCarouselRequestUrl = requestUrl;
		showLoadingState(carousel);

		const controller = new AbortController();
		requestControllers.set(carousel, controller);

		fetch(requestUrl, {
			method: "GET",
			mode: "cors",
			credentials: "omit",
			headers: {
				"Accept": "application/json"
			},
			signal: controller.signal
		})
			.then(function (response) {
				if (!response.ok) {
					throw new Error("Carousel request failed with status " + response.status + ".");
				}

				return response.json();
			})
			.then(function (payload) {
				if (requestControllers.get(carousel) !== controller) {
					return;
				}

				requestControllers.delete(carousel);
				renderCarousel(carousel, payload);
			})
			.catch(function (error) {
				if (error && error.name === "AbortError") {
					return;
				}

				if (requestControllers.get(carousel) !== controller) {
					return;
				}

				requestControllers.delete(carousel);
				showCarouselMessage(carousel, getErrorMessage(carousel));
				console.error("Unable to load carousel JSON endpoint.", error);
			});
	}

	function renderCarousel(selectorOrElement, payload) {
		const carousel = getCarouselElement(selectorOrElement);

		if (!carousel) {
			console.warn("Carousel element was not found.", selectorOrElement);
			return Promise.resolve();
		}

		const list = carousel.querySelector(".carousel-container");

		if (!list) {
			console.warn("Carousel list element was not found.", carousel);
			return Promise.resolve();
		}

		const renderToken = createCarouselRenderToken(carousel);
		const items = getItemsFromPayload(payload).filter(function (item) {
			return hasUsableCarouselItem(carousel, item);
		});

		return Promise.resolve()
			.then(function () {
				if (renderTokens.get(carousel) !== renderToken || carousel.getAttribute("data-clc-carousel-initialized") !== "true") {
					return;
				}

				finishRenderCarousel(carousel, list, items, payload);
			})
			.catch(function (error) {
				if (renderTokens.get(carousel) !== renderToken) {
					return;
				}

				showCarouselMessage(carousel, getErrorMessage(carousel));
				console.error("Unable to render carousel items.", error);
			});
	}

	function createCarouselRenderToken(carousel) {
		const renderToken = ++renderTokenIndex;
		renderTokens.set(carousel, renderToken);
		return renderToken;
	}

	function invalidateCarouselRender(carousel) {
		renderTokens.set(carousel, ++renderTokenIndex);
	}

	function finishRenderCarousel(carousel, list, items, payload) {
		const options = getCarouselOptions(carousel);

		list.innerHTML = "";
		carousel.clcCarouselItems = items;
		carousel.clcCarouselOptions = options;
		carousel.classList.remove("clc-carousel-loading");
		applyCarouselDisplayOptions(carousel, options);

		items.forEach(function (item) {
			list.appendChild(createSlide(carousel, item, options));
		});

		if (!items.length) {
			showCarouselMessage(carousel, getEmptyMessage(carousel));
			console.warn("No usable carousel items were found.", payload);
			return;
		}

		clearCarouselMessage(carousel);
		mountCarousel(carousel, carousel.clcCarouselOptions);
	}

	function getCarouselOptions(carousel) {
		return {
			showItemTitles: getBooleanDataAttribute(carousel, "data-show-item-titles", false),
			itemTitleLines: Math.min(getIntegerAttribute(carousel, "data-item-title-lines", 2, 1), 4)
		};
	}

	function applyCarouselDisplayOptions(carousel, options) {
		const normalizedOptions = normalizeCarouselOptions(options);
		const itemTitleHeight = getItemTitleHeight(normalizedOptions);
		const itemHeight = getCarouselItemHeight(normalizedOptions);

		carousel.classList.toggle("clc-carousel-show-item-titles", normalizedOptions.showItemTitles);
		setItemTitleLineClass(carousel, normalizedOptions.itemTitleLines);
		carousel.style.setProperty("--clc-carousel-item-title-lines", String(normalizedOptions.itemTitleLines));
		carousel.style.setProperty("--clc-carousel-item-title-height", itemTitleHeight + "px");
		carousel.style.setProperty("--clc-carousel-item-total-height", itemHeight + "px");

		applyCarouselTitleElementStyles(carousel, {
			itemWidth: getIntegerAttribute(carousel, "data-item-width", 100, 1),
			itemHeight: itemHeight
		}, normalizedOptions);
	}

	function normalizeCarouselOptions(options) {
		return {
			showItemTitles: Boolean(options && options.showItemTitles),
			itemTitleLines: Math.min(Math.max(options && options.itemTitleLines || 2, 1), 4)
		};
	}

	function setItemTitleLineClass(carousel, lineCount) {
		for (let i = 1; i <= 4; i++) {
			carousel.classList.remove("clc-carousel-item-title-lines-" + i);
		}

		carousel.classList.add("clc-carousel-item-title-lines-" + lineCount);
	}

	function getItemTitleHeight(options) {
		if (!options || !options.showItemTitles) {
			return 0;
		}

		return Math.ceil(12 * 1.2 * options.itemTitleLines);
	}

	function getCarouselItemHeight(options) {
		const coverHeight = 120;

		if (!options || !options.showItemTitles) {
			return coverHeight;
		}

		return coverHeight + 6 + getItemTitleHeight(options);
	}

	function getCarouselElements(selectorOrElement) {
		if (!selectorOrElement) {
			return Array.prototype.slice.call(document.querySelectorAll(".clc-carousel[data-rsid]"));
		}

		if (typeof selectorOrElement === "string") {
			return Array.prototype.slice.call(document.querySelectorAll(selectorOrElement));
		}

		if (isDomElement(selectorOrElement)) {
			return [selectorOrElement];
		}

		if (isElementCollection(selectorOrElement)) {
			return Array.prototype.slice.call(selectorOrElement).filter(isDomElement);
		}

		return [];
	}

	function getCarouselElement(selectorOrElement) {
		if (typeof selectorOrElement === "string") {
			return document.querySelector(selectorOrElement);
		}

		if (isDomElement(selectorOrElement)) {
			return selectorOrElement;
		}

		return null;
	}

	function isDomElement(value) {
		return Boolean(value && value.nodeType === 1 && typeof value.getAttribute === "function");
	}

	function isElementCollection(value) {
		return Boolean(
			value
			&& typeof value !== "string"
			&& typeof value !== "function"
			&& typeof value.length === "number"
			&& typeof value.getAttribute !== "function"
		);
	}

	function getItemsFromPayload(payload) {
		const data = parsePayload(payload);

		if (Array.isArray(data)) {
			return data;
		}

		if (data && Array.isArray(data.Items)) {
			return data.Items;
		}

		if (data && Array.isArray(data.items)) {
			return data.items;
		}

		return [];
	}

	function parsePayload(payload) {
		if (typeof payload !== "string") {
			return payload;
		}

		try {
			return JSON.parse(payload);
		} catch (error) {
			console.error("The carousel response was a string, but it was not valid JSON.", error, payload);
			return null;
		}
	}

	function hasUsableCarouselItem(carousel, item) {
		return Boolean(getSafeHttpUrl(getItemHref(item), getItemUrlBase(carousel)));
	}

	function createSlide(carousel, item, options) {
		const title = getItemTitle(item);
		const urlBase = getItemUrlBase(carousel);
		const href = getSafeHttpUrl(getItemHref(item), urlBase);
		const src = getSafeHttpUrl(getItemImageUrl(item), urlBase);
		const openInNewTab = shouldOpenInNewTab(carousel);
		const showItemTitles = Boolean(options && options.showItemTitles);

		const slide = document.createElement("li");
		slide.className = "splide__slide";

		const link = document.createElement("a");
		link.className = showItemTitles ? "carousel-link clc-carousel-link-with-title" : "carousel-link";
		link.href = href;
		link.title = title;
		link.setAttribute("aria-label", openInNewTab
			? "View catalog record for " + title + ", opens in a new tab"
			: "View catalog record for " + title);

		if (openInNewTab) {
			link.target = "_blank";
			link.rel = "noopener noreferrer";
		}

		const coverBox = document.createElement("span");
		coverBox.className = "carousel-cover-box";

		const titleFallback = document.createElement("span");
		titleFallback.className = "carousel-title-fallback";
		titleFallback.setAttribute("aria-hidden", "true");
		titleFallback.textContent = showItemTitles ? "No cover available" : title;

		const image = document.createElement("img");
		image.className = "carousel-image";
		image.alt = "";
		image.setAttribute("aria-hidden", "true");
		image.loading = "lazy";
		image.decoding = "async";

		image.addEventListener("load", function () {
			if (image.naturalWidth <= 1 && image.naturalHeight <= 1) {
				showTitleFallbackForLink(link);
			}

			queueCarouselRefresh(carousel);
		});

		image.addEventListener("error", function () {
			showTitleFallbackForLink(link);
			queueCarouselRefresh(carousel);
		});

		coverBox.appendChild(image);
		coverBox.appendChild(titleFallback);
		link.appendChild(coverBox);

		if (showItemTitles) {
			const itemTitle = document.createElement("span");
			itemTitle.className = "carousel-item-title";
			itemTitle.setAttribute("aria-hidden", "true");
			itemTitle.textContent = title;
			applyItemTitleStyles(itemTitle, options);
			link.appendChild(itemTitle);
		}

		slide.appendChild(link);

		if (src) {
			image.src = src;
		} else {
			showTitleFallbackForLink(link);
		}

		return slide;
	}

	function showTitleFallbackForLink(link) {
		link.classList.add("clc-carousel-cover-missing");
	}

	function getItemTitle(item) {
		const title = item && (item.Title || item.title);
		return normalizeDisplayText(title || "Catalog item") || "Catalog item";
	}

	function normalizeDisplayText(value) {
		if (value === null || value === undefined) {
			return "";
		}

		return String(value)
			.replace(/\\u\{([0-9a-fA-F]+)\}/g, function (match, hex) {
				const codePoint = parseInt(hex, 16);
				return Number.isFinite(codePoint) ? String.fromCodePoint(codePoint) : match;
			})
			.replace(/\\u([0-9a-fA-F]{4})/g, function (match, hex) {
				const codePoint = parseInt(hex, 16);
				return Number.isFinite(codePoint) ? String.fromCharCode(codePoint) : match;
			})
			.replace(/\\x([0-9a-fA-F]{2})/g, function (match, hex) {
				const codePoint = parseInt(hex, 16);
				return Number.isFinite(codePoint) ? String.fromCharCode(codePoint) : match;
			})
			.replace(/\\[nrt]/g, " ")
			.replace(/\\([^\w\s])/g, "$1")
			.replace(/\s+/g, " ")
			.trim();
	}

	function getItemHref(item) {
		return getItemStringValue(item, ["CatalogLink", "catalogLink", "Url", "url", "Link", "link"]);
	}

	function getItemImageUrl(item) {
		return getItemStringValue(item, ["ImageUrl", "imageUrl", "Image", "image", "CoverImage", "coverImage"]);
	}

	function getItemStringValue(item, names) {
		if (!item) {
			return "";
		}

		for (let i = 0; i < names.length; i++) {
			const value = item[names[i]];

			if (typeof value === "string" && value.trim()) {
				return value.trim();
			}
		}

		return "";
	}

	function getItemUrlBase(carousel) {
		try {
			const url = new URL(carousel.clcCarouselRequestUrl || getCarouselEndpoint(carousel), window.location.href);
			return url.origin + "/";
		} catch (error) {
			return window.location.href;
		}
	}

	function getSafeHttpUrl(value, baseUrl) {
		if (!value || !String(value).trim()) {
			return "";
		}

		try {
			const url = new URL(String(value).trim(), baseUrl || window.location.href);

			if (url.protocol === "http:" || url.protocol === "https:") {
				return url.toString();
			}
		} catch (error) {
			return "";
		}

		return "";
	}

	function shouldOpenInNewTab(carousel) {
		return getBooleanDataAttribute(carousel, "data-open-new-tab", false);
	}

	function mountCarousel(carousel, options) {
		if (!window.Splide || typeof window.Splide !== "function") {
			showCarouselMessage(carousel, getErrorMessage(carousel));
			console.error("Splide is not available.");
			return;
		}

		applyCarouselStyleOptions(carousel);
		applyCarouselDisplayOptions(carousel, carousel.clcCarouselOptions || getCarouselOptions(carousel));

		const layout = getResponsiveLayout(carousel);
		const existingInstance = splideInstances.get(carousel);
		const previousIndex = existingInstance ? existingInstance.index : 0;
		const layoutSignature = [
			layout.itemWidth,
			layout.itemHeight,
			layout.perPage,
			layout.frameWidth,
			layout.shellWidth,
			layout.canSlide
		].join("|");

		if (!layout.canMeasure) {
			carousel.clcCarouselNeedsMount = true;
			return;
		}

		delete carousel.clcCarouselNeedsMount;

		if (existingInstance && carousel.clcCarouselLayoutSignature === layoutSignature) {
			applyCarouselLayoutStyles(carousel, layout);
			existingInstance.refresh();
			applyCarouselLayoutStyles(carousel, layout);
			syncCarouselArrowState(carousel, existingInstance, layout);
			queueCarouselLayoutReapply(carousel, layout, existingInstance);
			return;
		}

		if (existingInstance) {
			existingInstance.destroy(true);
		}

		applyCarouselLayoutStyles(carousel, layout);
		carousel.classList.toggle("clc-carousel-no-arrows", !layout.canSlide);
		carousel.clcCarouselLayoutSignature = layoutSignature;

		const splide = new window.Splide(carousel, {
			type: "slide",
			fixedWidth: layout.itemWidth + "px",
			fixedHeight: layout.itemHeight + "px",
			perPage: layout.perPage,
			perMove: layout.perPage,
			gap: 0,
			arrows: layout.canSlide,
			pagination: false,
			drag: layout.canSlide,
			rewind: false,
			speed: shouldReduceMotion() ? 0 : 400
		});

		splide.on("mounted ready arrows:mounted arrows:updated updated resized refreshed moved move", function () {
			applyCarouselLayoutStyles(carousel, layout);
			syncCarouselArrowState(carousel, splide, layout);
			queueCarouselLayoutReapply(carousel, layout, splide);
		});

		splide.mount();
		applyCarouselLayoutStyles(carousel, layout);
		syncCarouselArrowState(carousel, splide, layout);
		queueCarouselLayoutReapply(carousel, layout, splide);

		if (previousIndex > 0 && carousel.clcCarouselItems && carousel.clcCarouselItems.length) {
			const maxStartIndex = Math.max(0, carousel.clcCarouselItems.length - layout.perPage);
			splide.go(Math.min(previousIndex, maxStartIndex));
			syncCarouselArrowState(carousel, splide, layout);
		}

		splideInstances.set(carousel, splide);
	}

	function getResponsiveLayout(carousel) {
		const options = carousel.clcCarouselOptions || getCarouselOptions(carousel);
		const itemWidth = getIntegerAttribute(carousel, "data-item-width", 100, 1);
		const itemHeight = getCarouselItemHeight(options);
		const maxItems = getIntegerAttribute(carousel, "data-max-items", 6, 1);
		const arrowGutter = getIntegerAttribute(carousel, "data-arrow-gutter", 50, 0);
		const availableWidth = getAvailableCarouselWidth(carousel);
		const itemCount = carousel.clcCarouselItems ? carousel.clcCarouselItems.length : maxItems;

		if (availableWidth <= 0) {
			return {
				itemWidth: itemWidth,
				itemHeight: itemHeight,
				perPage: 1,
				canSlide: itemCount > 1,
				canMeasure: false,
				frameWidth: itemWidth,
				shellWidth: itemWidth
			};
		}

		let usableTrackWidth = availableWidth - (arrowGutter * 2);
		let perPage = Math.floor(usableTrackWidth / itemWidth);
		let gutter = arrowGutter;

		if (perPage < 1) {
			perPage = 1;
			gutter = Math.max(0, Math.floor((availableWidth - itemWidth) / 2));
		}

		perPage = Math.min(perPage, maxItems);
		perPage = Math.min(perPage, Math.max(1, itemCount));

		const canSlide = itemCount > perPage;

		if (!canSlide) {
			gutter = 0;
		}

		return {
			itemWidth: itemWidth,
			itemHeight: itemHeight,
			perPage: perPage,
			canSlide: canSlide,
			canMeasure: true,
			frameWidth: perPage * itemWidth,
			shellWidth: (perPage * itemWidth) + (gutter * 2)
		};
	}

	function applyCarouselLayoutStyles(carousel, layout) {
		const frameWidth = layout.frameWidth + "px";
		const shellWidth = layout.shellWidth + "px";
		const itemWidth = layout.itemWidth + "px";
		const itemHeight = layout.itemHeight + "px";
		const title = carousel.querySelector(".clc-carousel-title");
		const shell = carousel.querySelector(".clc-carousel-shell");
		const frame = carousel.querySelector(".clc-carousel-frame");
		const track = carousel.querySelector(".splide__track");
		const list = carousel.querySelector(".splide__list");

		carousel.style.setProperty("--clc-carousel-frame-width", frameWidth);
		carousel.style.setProperty("--clc-carousel-shell-width", shellWidth);
		carousel.style.setProperty("--clc-carousel-item-width", itemWidth);
		carousel.style.setProperty("--clc-carousel-item-total-height", itemHeight);
		carousel.style.maxWidth = shellWidth;

		if (title) {
			title.style.width = frameWidth;
		}

		if (shell) {
			shell.style.width = shellWidth;
			shell.style.height = itemHeight;
		}

		if (frame) {
			frame.style.width = frameWidth;
			frame.style.height = itemHeight;
			frame.style.overflow = "hidden";
		}

		if (track) {
			track.style.width = frameWidth;
			track.style.height = itemHeight;
			track.style.overflow = "hidden";
		}

		if (list) {
			list.style.height = itemHeight;
		}

		carousel.querySelectorAll(".splide__slide").forEach(function (slide) {
			slide.style.height = itemHeight;
		});

		applyCarouselTitleElementStyles(carousel, layout, carousel.clcCarouselOptions || getCarouselOptions(carousel));
	}

	function queueCarouselLayoutReapply(carousel, layout, splide) {
		const reapply = function () {
			if (carousel.getAttribute("data-clc-carousel-initialized") !== "true") {
				return;
			}

			applyCarouselLayoutStyles(carousel, layout);

			if (splide) {
				syncCarouselArrowState(carousel, splide, layout);
			} else {
				applyRenderedArrowColors(carousel);
			}
		};

		if (typeof window.requestAnimationFrame === "function") {
			window.requestAnimationFrame(reapply);
		}

		window.setTimeout(reapply, 0);
		window.setTimeout(reapply, 100);
	}

	function applyCarouselTitleElementStyles(carousel, layout, options) {
		const normalizedOptions = normalizeCarouselOptions(options);
		const itemWidth = layout && layout.itemWidth ? layout.itemWidth : getIntegerAttribute(carousel, "data-item-width", 100, 1);
		const itemHeight = layout && layout.itemHeight ? layout.itemHeight : getCarouselItemHeight(normalizedOptions);
		const itemWidthValue = itemWidth + "px";
		const itemHeightValue = itemHeight + "px";
		const titleHeightValue = getItemTitleHeight(normalizedOptions) + "px";

		carousel.querySelectorAll(".carousel-link").forEach(function (link) {
			link.style.width = itemWidthValue;
			link.style.maxWidth = itemWidthValue;
			link.style.height = itemHeightValue;
			link.style.maxHeight = itemHeightValue;

			if (normalizedOptions.showItemTitles) {
				link.style.flexDirection = "column";
				link.style.alignItems = "center";
				link.style.justifyContent = "flex-start";
			}
		});

		carousel.querySelectorAll(".carousel-cover-box").forEach(function (coverBox) {
			coverBox.style.width = itemWidthValue;
			coverBox.style.maxWidth = itemWidthValue;
			coverBox.style.height = "120px";
			coverBox.style.maxHeight = "120px";
		});

		carousel.querySelectorAll(".carousel-item-title").forEach(function (itemTitle) {
			applyItemTitleStyles(itemTitle, normalizedOptions, itemWidthValue, titleHeightValue);
		});
	}

	function applyItemTitleStyles(itemTitle, options, itemWidthValue, titleHeightValue) {
		const normalizedOptions = normalizeCarouselOptions(options);
		const width = itemWidthValue || "var(--clc-carousel-item-width)";
		const height = titleHeightValue || getItemTitleHeight(normalizedOptions) + "px";

		itemTitle.style.display = "-webkit-box";
		itemTitle.style.width = width;
		itemTitle.style.maxWidth = width;
		itemTitle.style.height = height;
		itemTitle.style.maxHeight = height;
		itemTitle.style.minHeight = height;
		itemTitle.style.overflow = "hidden";
		itemTitle.style.webkitBoxOrient = "vertical";
		itemTitle.style.webkitLineClamp = String(normalizedOptions.itemTitleLines);
	}

	function getAvailableCarouselWidth(carousel) {
		if (!carousel.offsetParent && getComputedStyle(carousel).position !== "fixed") {
			return 0;
		}

		const parent = carousel.parentElement;
		const parentWidth = parent && parent.clientWidth > 0 ? parent.clientWidth : window.innerWidth;
		const viewportWidth = document.documentElement.clientWidth || window.innerWidth;
		const availableWidth = Math.min(parentWidth, viewportWidth);

		return Math.max(0, availableWidth);
	}

	function getIntegerAttribute(element, attributeName, fallback, minimum) {
		const rawValue = element.getAttribute(attributeName);

		if (!rawValue || !/^\d+$/.test(rawValue.trim())) {
			return fallback;
		}

		const value = Number(rawValue.trim());

		if (Number.isSafeInteger(value) && value >= minimum) {
			return value;
		}

		return fallback;
	}

	function getBooleanDataAttribute(element, attributeName, fallback) {
		const value = element.getAttribute(attributeName);

		if (value === null) {
			return fallback;
		}

		switch (value.trim().toLowerCase()) {
			case "true":
			case "1":
			case "yes":
				return true;
			case "false":
			case "0":
			case "no":
				return false;
			default:
				return fallback;
		}
	}

	function getLoadingMessage(carousel) {
		return carousel.getAttribute("data-loading-message") || "";
	}

	function getEmptyMessage(carousel) {
		return carousel.getAttribute("data-empty-message") || "No carousel items are currently available.";
	}

	function getErrorMessage(carousel) {
		return carousel.getAttribute("data-error-message") || "Carousel items could not be loaded.";
	}

	function showLoadingState(carousel) {
		const loadingMessage = getLoadingMessage(carousel);

		carousel.classList.add("clc-carousel-loading");

		if (loadingMessage) {
			showCarouselMessage(carousel, loadingMessage);
		}
	}

	function showCarouselMessage(carousel, message) {
		const existingInstance = splideInstances.get(carousel);
		const messageElement = carousel.querySelector(".clc-carousel-message");
		const list = carousel.querySelector(".carousel-container");

		if (existingInstance) {
			existingInstance.destroy(true);
			splideInstances.delete(carousel);
		}

		if (list) {
			list.innerHTML = "";
		}

		carousel.classList.remove("clc-carousel-loading");
		delete carousel.clcCarouselNeedsMount;
		carousel.classList.add("clc-carousel-has-message");
		carousel.classList.add("clc-carousel-no-arrows");

		if (messageElement) {
			messageElement.textContent = message;
			messageElement.hidden = false;
		}
	}

	function clearCarouselMessage(carousel) {
		const messageElement = carousel.querySelector(".clc-carousel-message");

		carousel.classList.remove("clc-carousel-has-message");
		carousel.classList.remove("clc-carousel-loading");

		if (messageElement) {
			messageElement.textContent = "";
			messageElement.hidden = true;
		}
	}

	function refreshCarousels(selectorOrElement) {
		return ensureCarouselDependenciesLoaded()
			.then(function () {
				refreshCarouselsAfterDependencies(selectorOrElement);
			})
			.catch(function (error) {
				handleDependencyLoadError(selectorOrElement, error);
			});
	}

	function refreshCarouselsAfterDependencies(selectorOrElement) {
		if (selectorOrElement) {
			const carousel = getCarouselElement(selectorOrElement);

			if (carousel && carousel.clcCarouselItems && carousel.clcCarouselItems.length) {
				mountCarousel(carousel, carousel.clcCarouselOptions || {});
			}

			return;
		}

		document.querySelectorAll(".clc-carousel[data-clc-carousel-initialized='true']").forEach(function (carousel) {
			if (carousel.clcCarouselItems && carousel.clcCarouselItems.length) {
				mountCarousel(carousel, carousel.clcCarouselOptions || {});
			}
		});
	}

	function reloadCarousels(selectorOrElement) {
		return ensureCarouselDependenciesLoaded()
			.then(function () {
				reloadCarouselsAfterDependencies(selectorOrElement);
			})
			.catch(function (error) {
				handleDependencyLoadError(selectorOrElement, error);
			});
	}

	function reloadCarouselsAfterDependencies(selectorOrElement) {
		if (selectorOrElement) {
			const carousel = getCarouselElement(selectorOrElement);

			if (carousel) {
				loadCarouselData(carousel);
			}

			return;
		}

		document.querySelectorAll(".clc-carousel[data-clc-carousel-initialized='true']").forEach(loadCarouselData);
	}

	function destroyCarousel(selectorOrElement) {
		if (!selectorOrElement) {
			document.querySelectorAll(".clc-carousel[data-clc-carousel-initialized='true']").forEach(destroyOneCarousel);
			return;
		}

		const carousel = getCarouselElement(selectorOrElement);

		if (carousel) {
			destroyOneCarousel(carousel);
		}
	}

	function destroyOneCarousel(carousel) {
		const splide = splideInstances.get(carousel);
		const observer = resizeObservers.get(carousel);
		const visibilityObserver = visibilityObservers.get(carousel);
		const timer = resizeTimers.get(carousel);

		abortCarouselRequest(carousel);
		invalidateCarouselRender(carousel);

		if (timer) {
			window.clearTimeout(timer);
			resizeTimers.delete(carousel);
		}

		if (splide) {
			splide.destroy(true);
			splideInstances.delete(carousel);
		}

		if (observer) {
			observer.disconnect();
			resizeObservers.delete(carousel);
		}

		if (visibilityObserver) {
			visibilityObserver.disconnect();
			visibilityObservers.delete(carousel);
		}

		carousel.classList.remove(
			"clc-carousel-loading",
			"clc-carousel-has-message",
			"clc-carousel-no-arrows",
			"clc-carousel-show-item-titles",
			"clc-carousel-item-title-lines-1",
			"clc-carousel-item-title-lines-2",
			"clc-carousel-item-title-lines-3",
			"clc-carousel-item-title-lines-4"
		);

		delete carousel.clcCarouselItems;
		delete carousel.clcCarouselOptions;
		delete carousel.clcCarouselLayoutSignature;
		delete carousel.clcCarouselArrowColors;
		delete carousel.clcCarouselRequestUrl;
		delete carousel.clcCarouselNeedsMount;
		carousel.style.removeProperty("--clc-carousel-item-title-lines");
		carousel.style.removeProperty("--clc-carousel-item-title-height");
		carousel.style.removeProperty("--clc-carousel-item-total-height");
		carousel.removeAttribute("data-clc-carousel-initialized");
	}

	function abortCarouselRequest(carousel) {
		const controller = requestControllers.get(carousel);

		if (controller) {
			controller.abort();
			requestControllers.delete(carousel);
		}
	}

	function observeCarouselSize(carousel) {
		if (!("ResizeObserver" in window)) {
			return;
		}

		if (resizeObservers.has(carousel)) {
			return;
		}

		const observer = new ResizeObserver(function () {
			queueCarouselRefresh(carousel);
		});

		observer.observe(carousel);

		if (carousel.parentElement) {
			observer.observe(carousel.parentElement);
		}

		resizeObservers.set(carousel, observer);
	}

	function observeCarouselVisibility(carousel) {
		if (!("IntersectionObserver" in window)) {
			return;
		}

		if (visibilityObservers.has(carousel)) {
			return;
		}

		const observer = new IntersectionObserver(function (entries) {
			entries.forEach(function (entry) {
				if (entry.isIntersecting && carousel.clcCarouselNeedsMount && carousel.clcCarouselItems && carousel.clcCarouselItems.length) {
					mountCarousel(carousel, carousel.clcCarouselOptions || {});
				}
			});
		});

		observer.observe(carousel);
		visibilityObservers.set(carousel, observer);
	}

	function queueCarouselRefresh(carousel) {
		const currentTimer = resizeTimers.get(carousel);

		if (currentTimer) {
			window.clearTimeout(currentTimer);
		}

		const nextTimer = window.setTimeout(function () {
			resizeTimers.delete(carousel);
			refreshCarouselsAfterDependencies(carousel);
		}, 150);

		resizeTimers.set(carousel, nextTimer);
	}

	function shouldReduceMotion() {
		return window.matchMedia && window.matchMedia("(prefers-reduced-motion: reduce)").matches;
	}

	function escapeHtml(value) {
		const div = document.createElement("div");
		div.textContent = value;
		return div.innerHTML;
	}

	function escapeAttribute(value) {
		return escapeHtml(value).replace(/"/g, "&quot;");
	}

	window.addEventListener("resize", function () {
		window.clearTimeout(windowResizeTimer);

		windowResizeTimer = window.setTimeout(function () {
			refreshCarouselsAfterDependencies();
		}, 150);
	});

	if (document.readyState === "loading") {
		document.addEventListener("DOMContentLoaded", function () {
			initCarousels();
		});
	} else {
		initCarousels();
	}
})();
