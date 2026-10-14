// Keeps the arrow keys, Escape and Enter from acting on the input (caret jump, form submit) while its suggestion list is in use.
// The list itself is driven by Blazor (aria-expanded / aria-activedescendant are set on the input at each render), but the
// server round trip is slower than the keyboard: a quick Enter after an arrow would reach the browser before aria-activedescendant
// is updated and submit the form. So the arrow navigation is also tracked here, without waiting for the server.
export function attach(input) {
  let navigated = false;

  input.addEventListener("input", () => {
    navigated = false;
  });
  input.addEventListener("blur", () => {
    navigated = false;
  });

  input.addEventListener("keydown", (event) => {
    if (input.getAttribute("aria-expanded") !== "true") {
      navigated = false;
      return;
    }

    if (event.key === "ArrowDown" || event.key === "ArrowUp") {
      event.preventDefault();
      // aria-controls is only set while the list has lines to move over.
      if (input.hasAttribute("aria-controls")) {
        navigated = true;
      }
    } else if (event.key === "Escape") {
      event.preventDefault();
      navigated = false;
    } else if (
      event.key === "Enter" &&
      (navigated || input.getAttribute("aria-activedescendant"))
    ) {
      event.preventDefault();
      navigated = false;
    }
  });
}

export function scrollOptionIntoView(id) {
  document.getElementById(id)?.scrollIntoView({ block: "nearest" });
}
