const statusFilter = document.querySelector("#status-filter");
const searchInput = document.querySelector("#repo-search-input");
const filterForm = document.querySelector("#repo-filter-form");
const tagPanel = document.querySelector("#tag-panel");
const noticeRegion = document.querySelector("#notice-region");
const submitForm = document.querySelector("#submit-image-form");
const submitDialog = document.querySelector("#submit-image-dialog");
const openSubmitDialogButton = document.querySelector("#open-submit-dialog");
const closeSubmitDialogButton = document.querySelector("#close-submit-dialog");
const repullForm = document.querySelector("#repull-form");
const removeImageForm = document.querySelector("#remove-image-form");
const repullSelectedButton = document.querySelector("#repull-selected-button");
const selectVisibleButton = document.querySelector("#select-visible-repos");
const repullVisibleButton = document.querySelector("#repull-visible-button");
const selectAllRepos = document.querySelector("#select-all-repos");
const repositoryPollIntervalMs = 15000;
let repositoryPollTimer = null;
let repositoryPollInFlight = false;

function escapeHtml(value) {
  return `${value ?? ""}`
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#039;");
}

function statusKey(status) {
  if (status === "已存在") {
    return "exists";
  }
  if (status === "未推送") {
    return "missing";
  }
  return "other";
}

function isReadyStatus(status) {
  return status === "已存在" || status === "可拉取";
}

function isPendingStatus(status) {
  return status === "等待 Action"
    || status === "Action 运行中"
    || status === "Action 成功"
    || status === "等待 ACR";
}

function applyRepoFilters(pushState = true) {
  const filter = statusFilter?.dataset.currentFilter || "all";
  const query = (searchInput?.value || "").trim().toLowerCase();
  const rows = [...document.querySelectorAll("[data-repo-row]")];
  let visibleCount = 0;

  for (const row of rows) {
    const matchesStatus = filter === "all"
      || row.dataset.status === filter
      || (filter === "tracking" && row.dataset.recentMirror === "true");
    const matchesSearch = !query || (row.dataset.search || "").toLowerCase().includes(query);
    const visible = matchesStatus && matchesSearch;
    row.hidden = !visible;
    if (visible) {
      visibleCount++;
    }
  }

  const filteredEmptyRow = document.querySelector("#filtered-empty-row");
  if (filteredEmptyRow) {
    filteredEmptyRow.hidden = visibleCount !== 0 || rows.length === 0;
  }

  if (pushState) {
    const url = new URL(window.location.href);
    url.searchParams.set("statusFilter", filter);
    if (query) {
      url.searchParams.set("search", searchInput.value.trim());
    } else {
      url.searchParams.delete("search");
    }
    window.history.replaceState({}, "", url);
  }

  updateSelectionState();
}

function setActiveFilter(filter) {
  if (!statusFilter) {
    return;
  }

  statusFilter.dataset.currentFilter = filter;
  for (const link of statusFilter.querySelectorAll("[data-filter]")) {
    link.classList.toggle("is-active", link.dataset.filter === filter);
  }
  applyRepoFilters();
}

function setNotice(type, message, linkUrl) {
  if (!noticeRegion) {
    return;
  }

  const link = linkUrl
    ? `<a href="${escapeHtml(linkUrl)}" target="_blank" rel="noopener noreferrer" data-open-commit>查看 Commit</a>`
    : "";
  noticeRegion.innerHTML = `<div class="notice ${type}"><span>${escapeHtml(message)}</span>${link}</div>`;
}

async function readJsonResponse(response, fallbackMessage) {
  const text = await response.text();
  if (!text.trim()) {
    return {
      ok: response.ok,
      message: response.ok ? fallbackMessage : `${fallbackMessage} HTTP ${response.status}`
    };
  }

  try {
    return JSON.parse(text);
  } catch {
    return {
      ok: false,
      message: `${fallbackMessage} 返回内容不是 JSON。HTTP ${response.status}`
    };
  }
}

function imageAddressFromSummary(summary, tag) {
  const target = `${summary || ""}`.split(" -> ").pop().replace(/^https?:\/\//i, "");
  const tagSeparator = target.lastIndexOf(":");
  const slash = target.lastIndexOf("/");
  const withoutTag = tagSeparator > slash ? target.slice(0, tagSeparator) : target;
  return `${withoutTag}:${tag}`;
}

function sourceImageFromSummary(summary) {
  return `${summary || ""}`.split(" -> ")[0].trim();
}

function repositorySummaryHtml(summary, sourceImage) {
  const parts = `${summary || ""}`.split(" -> ");
  const source = sourceImage || parts[0]?.trim() || "";
  const target = parts.length > 1 ? parts.slice(1).join(" -> ").trim() : "";
  const targetHtml = target ? `<span>${escapeHtml(target)}</span>` : "";
  return `<small class="repo-summary"><span>${escapeHtml(source)}</span>${targetHtml}</small>`;
}

function renderTagPanel(payload) {
  const repo = payload.repository;
  const tags = payload.tags || [];
  const sourceImage = repo.sourceImage || sourceImageFromSummary(repo.summary);
  const defaultButton = payload.defaultCopyAddress
    ? `<button type="button" class="copy-button" data-copy="${escapeHtml(payload.defaultCopyAddress)}">复制默认地址</button>`
    : "";

  const tagRows = tags.length === 0
    ? `<div class="empty-state compact"><h3>暂无 Tag</h3><p>Action 推送完成后会自动刷新。</p></div>`
    : tags.map(tag => `
        <article class="tag-row">
          <div>
            <strong>${escapeHtml(tag.tag)}</strong>
            <small>${escapeHtml(tag.digest)}</small>
          </div>
          <div class="tag-meta">
            <span class="${escapeHtml(tag.statusClass)}">${escapeHtml(tag.status)}</span>
            <button type="button" class="copy-button" data-copy="${escapeHtml(tag.copyAddress)}">复制</button>
          </div>
        </article>
      `).join("");

  tagPanel.innerHTML = `
    <div class="tag-panel-header">
      <div>
        <h3>${escapeHtml(repo.name)}</h3>
        <p>${escapeHtml(repo.namespace)}</p>
        <p>${escapeHtml(repo.workflowPlan)}</p>
        <p>${escapeHtml(repo.probePlan)}</p>
      </div>
      <div class="tag-panel-actions">
        <button type="button" class="copy-button" data-copy="${escapeHtml(sourceImage)}">复制源</button>
        <button type="button" class="copy-button" data-repull-source="${escapeHtml(sourceImage)}">重 pull</button>
        <button type="button" class="copy-button is-danger" data-remove-source="${escapeHtml(sourceImage)}" data-repo-label="${escapeHtml(repo.namespace)}/${escapeHtml(repo.name)}">移除</button>
        ${defaultButton}
        <span>${tags.length} Tag</span>
      </div>
    </div>
    <div class="tag-list">${tagRows}</div>
  `;
}

async function loadTagsForRow(row, pushState = true) {
  if (!row || !tagPanel) {
    return;
  }

  for (const current of document.querySelectorAll("[data-repo-row].is-active")) {
    current.classList.remove("is-active");
  }
  row.classList.add("is-active");

  tagPanel.innerHTML = `<div class="empty-state compact"><h3>加载中</h3><p>正在读取本地缓存中的 Tag。</p></div>`;

  const repoId = row.dataset.repoId;
  const url = `?handler=Tags&repoId=${encodeURIComponent(repoId)}`;
  const response = await fetch(url, {
    headers: {
      "Accept": "application/json",
      "X-Requested-With": "XMLHttpRequest"
    }
  });

  if (!response.ok) {
    const payload = await readJsonResponse(response, "Tag 加载失败。");
    tagPanel.innerHTML = `<div class="empty-state compact"><h3>加载失败</h3><p>${escapeHtml(payload.message)}</p></div>`;
    return;
  }

  renderTagPanel(await readJsonResponse(response, "Tag 加载失败。"));

  if (pushState) {
    const urlState = new URL(window.location.href);
    urlState.searchParams.set("repoId", row.dataset.repoId);
    urlState.searchParams.set("repoName", row.dataset.repoName);
    urlState.searchParams.set("repoNamespace", row.dataset.repoNamespace);
    window.history.replaceState({}, "", urlState);
  }
}

function upsertRepository(repository) {
  if (!repository) {
    return;
  }

  const tbody = document.querySelector("tbody");
  if (!tbody) {
    return;
  }

  const existing = document.querySelector(`[data-repo-row][data-repo-id="${CSS.escape(repository.repoId)}"]`);
  const wasChecked = existing?.querySelector("[data-repo-select]")?.checked ?? false;
  const previousStatus = existing?.dataset.statusText || "";
  const status = statusKey(repository.status);
  const sourceImage = repository.sourceImage || sourceImageFromSummary(repository.summary);
  const rowHtml = `
    <td class="select-cell">
      <input type="checkbox" form="repull-form" name="SelectedImages" value="${escapeHtml(sourceImage)}" data-repo-select aria-label="选择 ${escapeHtml(sourceImage)}" />
    </td>
    <td>
      <a class="repo-link" data-repo-link href="/?repoId=${encodeURIComponent(repository.repoId)}&repoName=${encodeURIComponent(repository.name)}&repoNamespace=${encodeURIComponent(repository.namespace)}">
        <strong>${escapeHtml(repository.namespace)}/${escapeHtml(repository.name)}</strong>
        ${repositorySummaryHtml(repository.summary, sourceImage)}
      </a>
    </td>
    <td>
      <span class="${escapeHtml(repository.statusClass)}" data-status-pill>${escapeHtml(repository.status)}</span>
      <small class="probe-plan" data-workflow-plan>${escapeHtml(repository.workflowPlan)}</small>
      <small class="probe-plan" data-probe-plan>${escapeHtml(repository.probePlan)}</small>
    </td>
    <td><button type="button" class="copy-button" data-copy="${escapeHtml(sourceImage)}">复制源</button></td>
    <td><button type="button" class="copy-button" data-copy="${escapeHtml(repository.copyAddress)}">复制预估</button></td>
    <td>
      <button type="button" class="copy-button" data-repull-source="${escapeHtml(sourceImage)}">重 pull</button>
      <button type="button" class="copy-button is-danger" data-remove-source="${escapeHtml(sourceImage)}" data-repo-label="${escapeHtml(repository.namespace)}/${escapeHtml(repository.name)}">移除</button>
    </td>
  `;

  const row = existing || document.createElement("tr");
  row.dataset.repoRow = "";
  row.dataset.repoId = repository.repoId;
  row.dataset.repoName = repository.name;
  row.dataset.repoNamespace = repository.namespace;
  row.dataset.sourceImage = sourceImage;
  row.dataset.status = status;
  row.dataset.statusText = repository.status;
  row.dataset.recentMirror = repository.isRecentMirror ? "true" : "false";
  row.dataset.pendingRefreshCount = `${repository.pendingRefreshCount ?? 0}`;
  row.dataset.search = `${repository.namespace}/${repository.name} ${repository.summary}`;
  row.innerHTML = rowHtml;
  row.classList.toggle("is-tracked", Boolean(repository.isRecentMirror));
  const checkbox = row.querySelector("[data-repo-select]");
  if (checkbox) {
    checkbox.checked = wasChecked;
  }

  if (!existing) {
    const filteredEmptyRow = document.querySelector("#filtered-empty-row");
    tbody.insertBefore(row, filteredEmptyRow);
  }

  if (previousStatus && !isReadyStatus(previousStatus) && isReadyStatus(repository.status)) {
    row.classList.add("is-fresh");
    window.setTimeout(() => row.classList.remove("is-fresh"), 2600);
  }

  applyRepoFilters(false);
  updateRepositoryPolling();
}

function selectedRepoCheckboxes() {
  return [...document.querySelectorAll("[data-repo-select]")];
}

function visibleRepoCheckboxes() {
  return selectedRepoCheckboxes().filter(checkbox => !checkbox.closest("[data-repo-row]")?.hidden);
}

function updateSelectionState() {
  const checkboxes = selectedRepoCheckboxes();
  const visible = visibleRepoCheckboxes();
  const selected = checkboxes.filter(checkbox => checkbox.checked);

  if (repullSelectedButton) {
    repullSelectedButton.disabled = selected.length === 0;
    repullSelectedButton.textContent = selected.length === 0 ? "重 pull 选中" : `重 pull ${selected.length} 个`;
  }

  if (selectAllRepos) {
    const visibleSelected = visible.filter(checkbox => checkbox.checked);
    selectAllRepos.checked = visible.length > 0 && visibleSelected.length === visible.length;
    selectAllRepos.indeterminate = visibleSelected.length > 0 && visibleSelected.length < visible.length;
  }

  if (repullVisibleButton) {
    repullVisibleButton.disabled = visible.length === 0;
  }
}

function repullFormDataFor(checkboxes) {
  const formData = new FormData(repullForm);
  formData.delete("SelectedImages");

  for (const checkbox of checkboxes) {
    formData.append("SelectedImages", checkbox.value);
  }

  return formData;
}

async function submitRepull(formData, button) {
  if (!repullForm) {
    return;
  }

  const originalText = button?.textContent;
  if (button) {
    button.disabled = true;
    button.textContent = "提交中...";
  }

  try {
    const response = await fetch(repullForm.action, {
      method: "POST",
      body: formData,
      headers: {
        "Accept": "application/json",
        "X-Requested-With": "XMLHttpRequest"
      }
    });

    const payload = await readJsonResponse(response, "重 pull 提交失败。");
    if (!response.ok || !payload.ok) {
      throw new Error(payload.message || "重 pull 提交失败。");
    }

    setNotice("success", payload.message, payload.commitUrl);
    for (const repository of payload.repositories || []) {
      upsertRepository(repository);
    }
    updateSelectionState();
    updateRepositoryPolling();
  } catch (error) {
    setNotice("error", error.message || "重 pull 提交失败。");
  } finally {
    if (button) {
      button.disabled = false;
      button.textContent = originalText;
    }
    updateSelectionState();
  }
}

async function submitRemoveImage(sourceImage, button) {
  if (!removeImageForm || !sourceImage) {
    return;
  }

  const repoLabel = button?.getAttribute("data-repo-label") || sourceImage;
  if (!window.confirm(`确认移除 ${repoLabel} 吗？\n\n会立刻清掉本地状态记录；GitHub images.txt 会在下次提交镜像时一并更新。`)) {
    return;
  }

  const originalText = button?.textContent;
  if (button) {
    button.disabled = true;
    button.textContent = "移除中...";
  }

  const formData = new FormData(removeImageForm);
  formData.set("sourceImage", sourceImage);

  try {
    const response = await fetch(removeImageForm.action, {
      method: "POST",
      body: formData,
      headers: {
        "Accept": "application/json",
        "X-Requested-With": "XMLHttpRequest"
      }
    });

    const payload = await readJsonResponse(response, "移除失败。");
    if (!response.ok || !payload.ok) {
      throw new Error(payload.message || "移除失败。");
    }

    for (const row of document.querySelectorAll("[data-repo-row]")) {
      if ((row.dataset.sourceImage || "").toLowerCase() === sourceImage.toLowerCase()) {
        row.remove();
      }
    }

    if (tagPanel) {
      tagPanel.innerHTML = `<div class="empty-state"><h3>未选择仓库</h3><p>左侧列表加载后，选择一个仓库查看远端 Tag。</p></div>`;
    }

    setNotice("success", payload.message, payload.commitUrl);
    applyRepoFilters(false);
    updateSelectionState();
    updateRepositoryPolling();
  } catch (error) {
    setNotice("error", error.message || "移除失败。");
  } finally {
    if (button) {
      button.disabled = false;
      button.textContent = originalText;
    }
  }
}

function pendingRepositoryRows() {
  return [...document.querySelectorAll("[data-repo-row]")]
    .filter(row => isPendingStatus(row.dataset.statusText || "")
      || Number(row.dataset.pendingRefreshCount || "0") > 0);
}

async function pollRepositories() {
  if (repositoryPollInFlight) {
    return;
  }

  repositoryPollInFlight = true;
  try {
    const response = await fetch("?handler=Repositories", {
      headers: {
        "Accept": "application/json",
        "X-Requested-With": "XMLHttpRequest"
      }
    });
    const payload = await readJsonResponse(response, "状态刷新失败。");
    if (!response.ok || !payload.ok) {
      throw new Error(payload.message || "状态刷新失败。");
    }

    let completedCount = 0;
    for (const repository of payload.repositories || []) {
      const existing = document.querySelector(`[data-repo-row][data-repo-id="${CSS.escape(repository.repoId)}"]`);
      const previousStatus = existing?.dataset.statusText || "";
      if (previousStatus && !isReadyStatus(previousStatus) && isReadyStatus(repository.status)) {
        completedCount++;
      }
      upsertRepository(repository);
    }

    if (completedCount > 0) {
      setNotice("success", `${completedCount} 个镜像状态已更新为已存在。`);
    }
  } catch (error) {
    setNotice("error", error.message || "状态刷新失败。");
  } finally {
    repositoryPollInFlight = false;
    updateRepositoryPolling();
  }
}

function updateRepositoryPolling() {
  const shouldPoll = pendingRepositoryRows().length > 0;
  if (shouldPoll && repositoryPollTimer === null) {
    repositoryPollTimer = window.setInterval(pollRepositories, repositoryPollIntervalMs);
  }

  if (!shouldPoll && repositoryPollTimer !== null) {
    window.clearInterval(repositoryPollTimer);
    repositoryPollTimer = null;
  }
}

document.addEventListener("click", async (event) => {
  const commitLink = event.target.closest("[data-open-commit]");
  if (commitLink) {
    event.preventDefault();
    window.open(commitLink.href, "_blank", "noopener,noreferrer");
    return;
  }

  const filterLink = event.target.closest("[data-filter]");
  if (filterLink) {
    event.preventDefault();
    setActiveFilter(filterLink.dataset.filter || "all");
    return;
  }

  const repoLink = event.target.closest("[data-repo-link]");
  if (repoLink) {
    event.preventDefault();
    await loadTagsForRow(repoLink.closest("[data-repo-row]"));
    return;
  }

  const repullButton = event.target.closest("[data-repull-source]");
  if (repullButton) {
    event.preventDefault();
    const sourceImage = repullButton.getAttribute("data-repull-source");
    if (!sourceImage || !repullForm) {
      return;
    }

    const formData = new FormData(repullForm);
    formData.delete("SelectedImages");
    formData.append("SelectedImages", sourceImage);
    await submitRepull(formData, repullButton);
    return;
  }

  const removeButton = event.target.closest("[data-remove-source]");
  if (removeButton) {
    event.preventDefault();
    await submitRemoveImage(removeButton.getAttribute("data-remove-source"), removeButton);
    return;
  }

  const button = event.target.closest("[data-copy]");
  if (!button) {
    return;
  }

  const value = button.getAttribute("data-copy");
  if (!value) {
    return;
  }

  const originalText = button.textContent;
  const markCopied = () => {
    button.textContent = "已复制";
    button.classList.add("is-copied");
    window.setTimeout(() => {
      button.textContent = originalText;
      button.classList.remove("is-copied");
    }, 1400);
  };

  try {
    await navigator.clipboard.writeText(value);
    markCopied();
  } catch {
    const textArea = document.createElement("textarea");
    textArea.value = value;
    textArea.setAttribute("readonly", "readonly");
    textArea.style.position = "fixed";
    textArea.style.left = "-9999px";
    document.body.appendChild(textArea);
    textArea.select();

    const copied = document.execCommand("copy");
    document.body.removeChild(textArea);
    if (copied) {
      markCopied();
    }
  }
});

filterForm?.addEventListener("submit", (event) => {
  event.preventDefault();
  applyRepoFilters();
});

searchInput?.addEventListener("input", () => {
  applyRepoFilters();
});

document.addEventListener("change", (event) => {
  if (event.target.matches("[data-repo-select]")) {
    updateSelectionState();
  }
});

selectVisibleButton?.addEventListener("click", () => {
  for (const checkbox of visibleRepoCheckboxes()) {
    checkbox.checked = true;
  }
  updateSelectionState();
});

selectAllRepos?.addEventListener("change", () => {
  for (const checkbox of visibleRepoCheckboxes()) {
    checkbox.checked = selectAllRepos.checked;
  }
  updateSelectionState();
});

repullForm?.addEventListener("submit", async (event) => {
  event.preventDefault();
  const selected = selectedRepoCheckboxes().filter(checkbox => checkbox.checked);
  await submitRepull(repullFormDataFor(selected), repullSelectedButton);
});

repullVisibleButton?.addEventListener("click", async () => {
  await submitRepull(repullFormDataFor(visibleRepoCheckboxes()), repullVisibleButton);
});

openSubmitDialogButton?.addEventListener("click", () => {
  if (submitDialog?.showModal) {
    submitDialog.showModal();
    submitDialog.querySelector("textarea")?.focus();
    return;
  }

  submitDialog?.removeAttribute("hidden");
});

closeSubmitDialogButton?.addEventListener("click", () => {
  if (submitDialog?.close) {
    submitDialog.close();
    return;
  }

  submitDialog?.setAttribute("hidden", "hidden");
});

submitForm?.addEventListener("submit", async (event) => {
  event.preventDefault();

  const button = submitForm.querySelector("button[type='submit']");
  const originalHtml = button?.innerHTML;
  if (button) {
    button.disabled = true;
    button.textContent = "提交中...";
  }

  try {
    const response = await fetch(submitForm.action, {
      method: "POST",
      body: new FormData(submitForm),
      headers: {
        "Accept": "application/json",
        "X-Requested-With": "XMLHttpRequest"
      }
    });

    const payload = await readJsonResponse(response, "提交失败。");
    if (!response.ok || !payload.ok) {
      throw new Error(payload.message || "提交失败。");
    }

    setNotice("success", payload.message, payload.commitUrl);
    if (payload.repositories?.length) {
      for (const repository of payload.repositories) {
        upsertRepository(repository);
      }
    } else if (payload.repository) {
      upsertRepository(payload.repository);
    }
    submitForm.reset();
    submitDialog?.close?.();
    updateRepositoryPolling();
  } catch (error) {
    setNotice("error", error.message || "提交失败。");
  } finally {
    if (button) {
      button.disabled = false;
      button.innerHTML = originalHtml;
    }
  }
});

applyRepoFilters(false);
updateSelectionState();
updateRepositoryPolling();
