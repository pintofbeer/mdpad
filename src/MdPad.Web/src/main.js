import { basicSetup, EditorView } from "codemirror";
import { markdown } from "@codemirror/lang-markdown";
import { oneDark } from "@codemirror/theme-one-dark";
import { icons } from "lucide";
import "./styles.css";

const app = document.querySelector("#app");
let bridge;

let state = {
  settings: { theme: "system" },
  sidebar: null,
  tabs: [],
  activeId: null,
  dirtyFiles: new Set(),
  activeSidebar: "recent",
  searchResults: [],
  selectedDate: null,
  selectedTag: null,
  editor: null,
  saveTimer: null,
  pendingEditorText: null,
};

start();

function start() {
  showBoot("Starting mdpad...");
  try {
    bridge = createBridge();
    renderShell();
    boot().catch(showFatal);
  } catch (error) {
    showFatal(error);
  }
}

async function boot() {
  const init = await bridge.send("init");
  state.settings = init.settings;
  state.sidebar = init.sidebar;
  applyTheme();
  await openNote(init.scratch, false);
  render();
}

function createBridge() {
  let nextId = 1;
  const pending = new Map();

  const receive = (raw) => {
    const message = typeof raw === "string" ? JSON.parse(raw) : raw;
    const resolver = pending.get(message.id);
    if (!resolver) return;
    pending.delete(message.id);
    message.ok ? resolver.resolve(message.data) : resolver.reject(new Error(message.error));
  };

  try {
    if (window.chrome?.webview) {
      window.chrome.webview.addEventListener("message", (event) => receive(event.data));
    }

    if (window.external && typeof window.external.receiveMessage === "function") {
      window.external.receiveMessage(receive);
    }
  } catch (error) {
    throw new Error(`Could not register desktop bridge: ${error.message}`);
  }

  return {
    send(type, payload = {}) {
      const canUseWebView2 = Boolean(window.chrome?.webview);
      const canUsePhotino = Boolean(window.external && typeof window.external.sendMessage === "function");
      if (!canUseWebView2 && !canUsePhotino) {
        return Promise.reject(new Error("mdpad must run inside the desktop shell."));
      }

      const id = String(nextId++);
      const message = { id, type, payload };
      if (canUseWebView2) {
        window.chrome.webview.postMessage(message);
      } else {
        window.external.sendMessage(JSON.stringify(message));
      }
      return new Promise((resolve, reject) => pending.set(id, { resolve, reject }));
    },
  };
}

function showBoot(message) {
  app.innerHTML = `
    <div style="display:grid;place-items:center;height:100vh;font:14px Segoe UI,system-ui,sans-serif;color:#3b4452;background:#f7f8fa">
      <div>${escapeHtml(message)}</div>
    </div>
  `;
}

function showFatal(error) {
  const message = error instanceof Error ? error.message : String(error);
  app.innerHTML = `
    <div style="display:grid;place-items:center;height:100vh;padding:24px;font:14px Segoe UI,system-ui,sans-serif;color:#171a1f;background:#f7f8fa">
      <div style="max-width:720px;border:1px solid #d7dce3;background:white;border-radius:8px;padding:18px;box-shadow:0 1px 2px rgba(16,24,40,.08)">
        <h1 style="margin:0 0 8px;font-size:18px">mdpad could not start</h1>
        <pre style="white-space:pre-wrap;margin:0;color:#b42318">${escapeHtml(message)}</pre>
      </div>
    </div>
  `;
  console.error(error);
}

function renderShell() {
  app.innerHTML = `
    <aside class="sidebar">
      <div class="brand">
        <div class="brand-mark">m</div>
        <div>
          <div class="brand-name">mdpad</div>
          <div class="brand-subtitle">daily notes</div>
        </div>
      </div>
      <div class="search-row">
        <span class="search-icon">${icon("search")}</span>
        <input id="search" type="search" placeholder="Search notes, tags, dates" />
      </div>
      <div class="quick-actions">
        <button id="todayBtn" title="Today">${icon("calendar-days")}<span>Today</span></button>
        <button id="newBtn" title="New tab">${icon("plus")}<span>New</span></button>
        <button id="openFileBtn" title="Open file">${icon("folder-open")}<span>Open</span></button>
      </div>
      <nav class="sidebar-tabs" aria-label="Sidebar views">
        <button data-side="recent" class="active">Recent</button>
        <button data-side="dates">Dates</button>
        <button data-side="tags">Tags</button>
      </nav>
      <div id="sidebarContent" class="sidebar-content"></div>
    </aside>
    <main class="workspace">
      <div class="toolbar">
        <div id="tabbar" class="tabbar"></div>
        <div class="toolbar-actions">
          <button id="saveBtn" class="icon-button" title="Save file">${icon("save")}</button>
          <button id="saveAsBtn" class="icon-button" title="Save as">${icon("save-all")}</button>
          <select id="themeSelect" title="Theme">
            <option value="system">System</option>
            <option value="light">Light</option>
            <option value="dark">Dark</option>
          </select>
        </div>
      </div>
      <section class="meta">
        <input id="titleInput" class="title-input" />
        <input id="dateInput" class="date-input" type="date" />
        <input id="tagsInput" class="tags-input" placeholder="tags" />
        <div id="fileState" class="file-state"></div>
      </section>
      <div id="editorHost" class="editor-host"></div>
    </main>
  `;

  document.querySelector("#search").addEventListener("input", debounce(onSearch, 180));
  document.querySelector("#todayBtn").addEventListener("click", openToday);
  document.querySelector("#newBtn").addEventListener("click", createNote);
  document.querySelector("#openFileBtn").addEventListener("click", openFile);
  document.querySelector("#saveBtn").addEventListener("click", saveFile);
  document.querySelector("#saveAsBtn").addEventListener("click", saveAs);
  document.querySelector("#themeSelect").addEventListener("input", setTheme);
  document.querySelector("#themeSelect").addEventListener("change", setTheme);
  document.querySelector("#titleInput").addEventListener("change", saveMetadata);
  document.querySelector("#dateInput").addEventListener("change", saveMetadata);
  document.querySelector("#tagsInput").addEventListener("change", saveMetadata);
  document.querySelectorAll("[data-side]").forEach((button) => {
    button.addEventListener("click", () => {
      state.activeSidebar = button.dataset.side;
      state.selectedDate = null;
      state.selectedTag = null;
      render();
    });
  });

  window.addEventListener("keydown", (event) => {
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === "s") {
      event.preventDefault();
      saveFile();
    }
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === "o") {
      event.preventDefault();
      openFile();
    }
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === "n") {
      event.preventDefault();
      createNote();
    }
  });
}

function mountEditor(note) {
  const host = document.querySelector("#editorHost");
  host.innerHTML = "";
  const extensions = [
    basicSetup,
    markdown(),
    EditorView.lineWrapping,
    EditorView.updateListener.of((update) => {
      if (!update.docChanged) return;
      const text = update.state.doc.toString();
      state.pendingEditorText = text;
      const tab = getActiveTab();
      if (tab?.isFile) state.dirtyFiles.add(tab.id);
      scheduleAutosave();
      renderTabsAndMeta();
    }),
  ];

  if (isDark()) {
    extensions.push(oneDark);
  }

  state.editor = new EditorView({
    doc: note.content,
    extensions,
    parent: host,
  });
}

async function openToday() {
  const note = await bridge.send("openTodayScratch");
  await openNote(note);
}

async function createNote() {
  const today = isoToday();
  const count = state.tabs.filter((tab) => tab.noteDate === today).length + 1;
  const note = await bridge.send("createNote", { noteDate: today, title: `note ${count}` });
  await openNote(note);
}

async function openFile() {
  try {
    const note = await bridge.send("openFile");
    await openNote(note);
  } catch (error) {
    toast(error.message);
  }
}

async function openNote(summaryOrNote, refreshSidebar = true) {
  const note = summaryOrNote.content === undefined
    ? await bridge.send("getNote", { id: summaryOrNote.id })
    : summaryOrNote;
  const existing = state.tabs.findIndex((tab) => tab.id === note.id);
  if (existing >= 0) {
    state.tabs[existing] = note;
  } else {
    state.tabs.push(note);
  }
  state.activeId = note.id;
  state.pendingEditorText = note.content;
  mountEditor(note);
  if (refreshSidebar) await refreshSidebarModel();
  render();
}

async function closeTab(id, event) {
  event?.stopPropagation();
  await flushAutosave();
  await bridge.send("closeNote", { id });
  state.tabs = state.tabs.filter((tab) => tab.id !== id);
  state.dirtyFiles.delete(id);
  if (state.activeId === id) {
    const next = state.tabs.at(-1);
    state.activeId = next?.id ?? null;
    if (next) mountEditor(next);
    else await openToday();
  }
  await refreshSidebarModel();
  render();
}

async function saveMetadata() {
  const tab = getActiveTab();
  if (!tab) return;
  const title = document.querySelector("#titleInput").value;
  const noteDate = document.querySelector("#dateInput").value;
  const tags = parseTags(document.querySelector("#tagsInput").value);
  const note = await bridge.send("saveMetadata", { id: tab.id, title, noteDate, tags });
  replaceTab(note);
  await refreshSidebarModel();
  render();
}

function scheduleAutosave() {
  clearTimeout(state.saveTimer);
  state.saveTimer = setTimeout(flushAutosave, 450);
}

async function flushAutosave() {
  const tab = getActiveTab();
  if (!tab || state.pendingEditorText === tab.content) return;
  const note = await bridge.send("saveContent", { id: tab.id, content: state.pendingEditorText });
  replaceTab(note);
  await refreshSidebarModel();
}

async function saveFile() {
  await flushAutosave();
  const tab = getActiveTab();
  if (!tab) return;
  try {
    const note = await bridge.send("saveFile", { id: tab.id });
    state.dirtyFiles.delete(tab.id);
    replaceTab(note);
    await refreshSidebarModel();
    render();
  } catch (error) {
    toast(error.message);
  }
}

async function saveAs() {
  await flushAutosave();
  const tab = getActiveTab();
  if (!tab) return;
  try {
    const note = await bridge.send("saveAs", { id: tab.id });
    state.dirtyFiles.delete(tab.id);
    replaceTab(note);
    await refreshSidebarModel();
    render();
  } catch (error) {
    toast(error.message);
  }
}

async function onSearch(event) {
  const query = event.target.value.trim();
  state.activeSidebar = query ? "search" : "recent";
  state.searchResults = query ? await bridge.send("search", { query }) : [];
  renderSidebar();
}

async function setTheme(event) {
  const theme = event.target.value;
  state.settings = { theme };
  applyTheme();
  renderTabsAndMeta();
  const tab = getActiveTab();
  if (tab) mountEditor({ ...tab, content: state.editor?.state.doc.toString() ?? tab.content });
  try {
    state.settings = await bridge.send("setTheme", { theme });
  } catch (error) {
    showFatal(error);
    return;
  }
  document.querySelector("#themeSelect").value = state.settings.theme;
}

function replaceTab(note) {
  const index = state.tabs.findIndex((tab) => tab.id === note.id);
  if (index >= 0) state.tabs[index] = note;
  state.pendingEditorText = note.content;
}

async function refreshSidebarModel() {
  state.sidebar = await bridge.send("sidebar");
}

function render() {
  renderTabsAndMeta();
  renderSidebar();
  document.querySelector("#themeSelect").value = state.settings.theme;
  document.querySelectorAll("[data-side]").forEach((button) => {
    button.classList.toggle("active", button.dataset.side === state.activeSidebar);
  });
}

function renderTabsAndMeta() {
  const tabbar = document.querySelector("#tabbar");
  tabbar.innerHTML = state.tabs.map((tab) => `
    <button class="tab ${tab.id === state.activeId ? "active" : ""}" data-tab="${tab.id}" title="${escapeHtml(tab.title)}">
      <span>${escapeHtml(tab.title)}${state.dirtyFiles.has(tab.id) ? " *" : ""}</span>
      ${tab.isFile ? icon("file-text") : ""}
      <span class="tab-close" data-close="${tab.id}">${icon("x")}</span>
    </button>
  `).join("");
  tabbar.querySelectorAll("[data-tab]").forEach((button) => {
    button.addEventListener("click", () => activateTab(button.dataset.tab));
  });
  tabbar.querySelectorAll("[data-close]").forEach((button) => {
    button.addEventListener("click", (event) => closeTab(button.dataset.close, event));
  });

  const tab = getActiveTab();
  document.querySelector("#titleInput").value = tab?.title ?? "";
  document.querySelector("#dateInput").value = tab?.noteDate ?? isoToday();
  document.querySelector("#tagsInput").value = tab?.tags?.join(", ") ?? "";
  document.querySelector("#fileState").textContent = tab?.isFile
    ? `${state.dirtyFiles.has(tab.id) ? "Unsaved file" : "File"}: ${tab.filePath}`
    : "Database note";
}

function renderSidebar() {
  const container = document.querySelector("#sidebarContent");
  if (!state.sidebar) return;

  if (state.activeSidebar === "search") {
    container.innerHTML = renderNoteList("Search", state.searchResults);
    bindNoteList(container);
    return;
  }

  if (state.activeSidebar === "dates") {
    container.innerHTML = `
      <div class="section-title">Dates</div>
      <div class="date-list">
        ${state.sidebar.dates.map((bucket) => `
          <button class="date-row ${state.selectedDate === bucket.date ? "active" : ""}" data-date="${bucket.date}">
            <span>${formatDate(bucket.date)}</span><strong>${bucket.count}</strong>
          </button>
          ${state.selectedDate === bucket.date ? renderCompactNotes(bucket.notes) : ""}
        `).join("")}
      </div>
    `;
    container.querySelectorAll("[data-date]").forEach((button) => {
      button.addEventListener("click", () => {
        state.selectedDate = state.selectedDate === button.dataset.date ? null : button.dataset.date;
        renderSidebar();
      });
    });
    bindNoteList(container);
    return;
  }

  if (state.activeSidebar === "tags") {
    const notes = state.selectedTag
      ? state.sidebar.recent.concat(state.sidebar.dates.flatMap((bucket) => bucket.notes))
          .filter((note, index, all) => all.findIndex((item) => item.id === note.id) === index)
          .filter((note) => note.tags.some((tag) => tag.toLowerCase() === state.selectedTag.toLowerCase()))
      : [];
    container.innerHTML = `
      <div class="section-title">Tags</div>
      <div class="tag-cloud">
        ${state.sidebar.tags.map((tag) => `
          <button class="tag-pill ${state.selectedTag === tag.name ? "active" : ""}" data-tag="${escapeHtml(tag.name)}">
            #${escapeHtml(tag.name)} <strong>${tag.count}</strong>
          </button>
        `).join("")}
      </div>
      ${state.selectedTag ? renderNoteList(`#${state.selectedTag}`, notes) : ""}
    `;
    container.querySelectorAll("[data-tag]").forEach((button) => {
      button.addEventListener("click", () => {
        state.selectedTag = state.selectedTag === button.dataset.tag ? null : button.dataset.tag;
        renderSidebar();
      });
    });
    bindNoteList(container);
    return;
  }

  container.innerHTML = renderNoteList("Recent", state.sidebar.recent);
  bindNoteList(container);
}

function renderNoteList(title, notes) {
  return `
    <div class="section-title">${escapeHtml(title)}</div>
    ${renderCompactNotes(notes)}
  `;
}

function renderCompactNotes(notes) {
  if (!notes.length) return `<div class="empty-state">No notes</div>`;
  return `<div class="note-list">${notes.map((note) => `
    <button class="note-row ${note.id === state.activeId ? "active" : ""}" data-note="${note.id}">
      <span class="note-title">${escapeHtml(note.title)}</span>
      <span class="note-date">${formatDate(note.noteDate)}${note.isFile ? " · file" : ""}</span>
      <span class="note-preview">${escapeHtml(note.preview || "")}</span>
    </button>
  `).join("")}</div>`;
}

function bindNoteList(container) {
  container.querySelectorAll("[data-note]").forEach((button) => {
    button.addEventListener("click", async () => {
      const note = await bridge.send("getNote", { id: button.dataset.note });
      await openNote(note);
    });
  });
}

function activateTab(id) {
  const tab = state.tabs.find((item) => item.id === id);
  if (!tab) return;
  state.activeId = id;
  mountEditor(tab);
  render();
}

function getActiveTab() {
  return state.tabs.find((tab) => tab.id === state.activeId);
}

function applyTheme() {
  const theme = isDark() ? "dark" : "light";
  document.documentElement.dataset.theme = theme;
  document.documentElement.style.colorScheme = theme;
}

function isDark() {
  return state.settings.theme === "dark" ||
    (state.settings.theme === "system" && window.matchMedia("(prefers-color-scheme: dark)").matches);
}

function parseTags(value) {
  return value.split(/[,\s]+/).map((tag) => tag.trim().replace(/^#/, "")).filter(Boolean);
}

function icon(name) {
  const pascalName = name.split("-").map((part) => part.charAt(0).toUpperCase() + part.slice(1)).join("");
  const iconNode = icons[pascalName];
  if (!iconNode) return "";
  return renderIconNode(withIconAttrs(iconNode, { width: 16, height: 16, "stroke-width": 2 }));
}

function withIconAttrs(iconNode, attrs) {
  const [tag, nodeAttrs, ...children] = iconNode;
  return [tag, { ...nodeAttrs, ...attrs }, ...children];
}

function renderIconNode(node) {
  const [tag, attrs = {}, ...children] = node;
  const attrText = Object.entries(attrs)
    .map(([key, value]) => `${key}="${escapeHtml(value)}"`)
    .join(" ");
  return `<${tag}${attrText ? ` ${attrText}` : ""}>${children.map(renderIconNode).join("")}</${tag}>`;
}

function debounce(fn, ms) {
  let timer;
  return (...args) => {
    clearTimeout(timer);
    timer = setTimeout(() => fn(...args), ms);
  };
}

function toast(message) {
  if (!message || message.includes("cancelled")) return;
  console.error(message);
}

function isoToday() {
  return new Date().toISOString().slice(0, 10);
}

function formatDate(value) {
  return new Intl.DateTimeFormat(undefined, { month: "short", day: "numeric", year: "numeric" }).format(new Date(`${value}T00:00:00`));
}

function escapeHtml(value) {
  return String(value).replace(/[&<>"']/g, (char) => ({
    "&": "&amp;",
    "<": "&lt;",
    ">": "&gt;",
    "\"": "&quot;",
    "'": "&#039;",
  }[char]));
}
