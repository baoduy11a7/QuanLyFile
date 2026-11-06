/**
 * sync-invoice-handler.js
 * Quản lý Cửa sổ Đồng bộ hóa đơn tự động kết nối ngầm Cổng GOV (Không cần nhập mật khẩu hay Captcha)
 * FDI Accounting and Tax Pro v3.3.16
 */

(function () {
    var syncPollingTimer = null;
    var activeSyncJobId = null;

    // Helper: Định dạng ngày Date -> dd/MM/yyyy
    function formatDateDMY(d) {
        var day = String(d.getDate()).padStart(2, '0');
        var month = String(d.getMonth() + 1).padStart(2, '0');
        var year = d.getFullYear();
        return day + '/' + month + '/' + year;
    }

    // Helper: Định dạng ngày Date -> yyyy-MM-dd cho input[type="date"]
    function formatDateYMD(d) {
        var year = d.getFullYear();
        var month = String(d.getMonth() + 1).padStart(2, '0');
        var day = String(d.getDate()).padStart(2, '0');
        return year + '-' + month + '-' + day;
    }

    var flatpickrInstance = null;

    // Khởi tạo Flatpickr chọn khoảng ngày hoặc 1 ngày duy nhất với định dạng dd/MM/yyyy
    function initSyncDateRangePicker() {
        var inputEl = document.getElementById('syncDateRange');
        if (!inputEl) return;

        var now = new Date();
        var past30Days = new Date();
        past30Days.setDate(now.getDate() - 30);

        if (typeof flatpickr !== 'undefined') {
            if (!flatpickrInstance) {
                var vnLocale = (typeof flatpickr.l10ns !== 'undefined' && flatpickr.l10ns.vn) ? flatpickr.l10ns.vn : {
                    rangeSeparator: " đến ",
                    weekdays: {
                        shorthand: ["CN", "T2", "T3", "T4", "T5", "T6", "T7"],
                        longhand: ["Chủ Nhật", "Thứ Hai", "Thứ Ba", "Thứ Tư", "Thứ Năm", "Thứ Sáu", "Thứ Bảy"]
                    },
                    months: {
                        shorthand: ["Th1", "Th2", "Th3", "Th4", "Th5", "Th6", "Th7", "Th8", "Th9", "Th10", "Th11", "Th12"],
                        longhand: ["Tháng 1", "Tháng 2", "Tháng 3", "Tháng 4", "Tháng 5", "Tháng 6", "Tháng 7", "Tháng 8", "Tháng 9", "Tháng 10", "Tháng 11", "Tháng 12"]
                    }
                };

                flatpickrInstance = flatpickr(inputEl, {
                    mode: "range",
                    dateFormat: "d/m/Y",
                    locale: vnLocale,
                    defaultDate: [past30Days, now],
                    allowInput: false,
                    onChange: function (selectedDates) {
                        if (selectedDates && selectedDates.length > 0) {
                            var from = selectedDates[0];
                            var to = selectedDates.length > 1 ? selectedDates[1] : selectedDates[0];
                            $('#syncFromDate').val(formatDateDMY(from));
                            $('#syncToDate').val(formatDateDMY(to));
                        }
                    },
                    onClose: function (selectedDates) {
                        if (selectedDates && selectedDates.length > 0) {
                            var from = selectedDates[0];
                            var to = selectedDates.length > 1 ? selectedDates[1] : selectedDates[0];
                            $('#syncFromDate').val(formatDateDMY(from));
                            $('#syncToDate').val(formatDateDMY(to));
                        }
                    }
                });
            }

            $('#syncFromDate').val(formatDateDMY(past30Days));
            $('#syncToDate').val(formatDateDMY(now));
        } else {
            // Fallback nếu Flatpickr chưa sẵn sàng
            var defText = formatDateDMY(past30Days) + ' đến ' + formatDateDMY(now);
            $('#syncDateRange').val(defText);
            $('#syncFromDate').val(formatDateDMY(past30Days));
            $('#syncToDate').val(formatDateDMY(now));
        }
    }

    // Chọn nhanh các mốc thời gian phổ biến
    window.setQuickDateRange = function (type) {
        var now = new Date();
        var from = new Date();
        var to = new Date();

        if (type === '30days') {
            from.setDate(now.getDate() - 30);
            to = now;
        } else if (type === 'thisMonth') {
            from = new Date(now.getFullYear(), now.getMonth(), 1);
            to = new Date(now.getFullYear(), now.getMonth() + 1, 0);
        } else if (type === 'prevMonth') {
            from = new Date(now.getFullYear(), now.getMonth() - 1, 1);
            to = new Date(now.getFullYear(), now.getMonth(), 0);
        } else if (type === 'thisQuarter') {
            var q = Math.floor(now.getMonth() / 3);
            from = new Date(now.getFullYear(), q * 3, 1);
            to = new Date(now.getFullYear(), (q + 1) * 3, 0);
        }

        if (flatpickrInstance) {
            flatpickrInstance.setDate([from, to], true);
        } else {
            $('#syncDateRange').val(formatDateDMY(from) + ' đến ' + formatDateDMY(to));
        }

        $('#syncFromDate').val(formatDateDMY(from));
        $('#syncToDate').val(formatDateDMY(to));
    };

    // Mở modal Đồng bộ hóa đơn
    window.openSyncInvoiceModal = function () {
        // Khởi tạo date picker duy nhất
        initSyncDateRangePicker();

        // Ẩn alert và progress nếu còn dư từ lần trước
        $('#syncModalAlert').addClass('d-none').text('');
        $('#syncLiveProgressBox').addClass('d-none');
        $('#syncLiveSpinner').removeClass('d-none');
        $('#syncLiveSuccessIcon').addClass('d-none');
        $('#syncLiveErrorIcon').addClass('d-none');
        resetSyncButtons();

        // Mở Modal
        var modalEl = document.getElementById('modalSyncInvoices');
        if (modalEl && typeof bootstrap !== 'undefined' && bootstrap.Modal) {
            try {
                var bsModal = bootstrap.Modal.getInstance(modalEl) || new bootstrap.Modal(modalEl);
                bsModal.show();
            } catch (e) {
                $('#modalSyncInvoices').modal('show');
            }
        } else {
            $('#modalSyncInvoices').modal('show');
        }

        // Tải lịch sử đồng bộ
        loadSyncHistory();
    };

    // Tải lịch sử đồng bộ cho công ty hiện tại
    window.loadSyncHistory = function (silent) {
        if (!silent) {
            $('#tbodySyncHistory').html('<tr><td colspan="7" class="text-center py-4 text-muted"><div class="spinner-border spinner-border-sm text-secondary me-2" role="status"></div>Đang tải lịch sử đồng bộ...</td></tr>');
        }

        $.ajax({
            url: '/Import/GetSyncHistory',
            type: 'GET',
            cache: false,
            success: function (res) {
                if (res && res.success) {
                    if (res.companyName) {
                        $('#syncModalCompanyName').text(res.companyName);
                    }

                    var items = res.data || [];
                    if (items.length === 0) {
                        $('#tbodySyncHistory').html('<tr><td colspan="7" class="text-center py-4 text-muted">Không có dữ liệu</td></tr>');
                        return;
                    }

                    var html = '';
                    for (var i = 0; i < items.length; i++) {
                        var it = items[i];
                        var statusBadge = '';
                        if (it.status === 'Thành công') {
                            statusBadge = '<span class="badge bg-success-subtle text-success border border-success-subtle fw-medium px-2 py-1"><i class="bi bi-check-circle me-1"></i>Thành công</span>';
                        } else if (it.status === 'Cảnh báo') {
                            statusBadge = '<span class="badge bg-warning-subtle text-warning border border-warning-subtle fw-medium px-2 py-1"><i class="bi bi-exclamation-triangle me-1"></i>Cảnh báo</span>';
                        } else {
                            statusBadge = '<span class="badge bg-danger-subtle text-danger border border-danger-subtle fw-medium px-2 py-1"><i class="bi bi-x-circle me-1"></i>Thất bại</span>';
                        }

                        html += '<tr>' +
                            '<td class="text-center text-muted fw-semibold">' + (it.stt || (i + 1)) + '</td>' +
                            '<td>' + (it.fromDate || '-') + '</td>' +
                            '<td>' + (it.toDate || '-') + '</td>' +
                            '<td class="text-center fw-bold text-dark">' + (it.syncedCount || 0) + '</td>' +
                            '<td class="text-center">' + statusBadge + '</td>' +
                            '<td class="text-truncate" style="max-width: 280px;" title="' + (it.description || '') + '">' + (it.description || '-') + '</td>' +
                            '<td class="text-center text-muted small">' + (it.syncedAt || '-') + '</td>' +
                            '</tr>';
                    }

                    $('#tbodySyncHistory').html(html);
                } else {
                    $('#tbodySyncHistory').html('<tr><td colspan="7" class="text-center py-3 text-muted">Không có dữ liệu</td></tr>');
                }
            },
            error: function () {
                $('#tbodySyncHistory').html('<tr><td colspan="7" class="text-center py-3 text-danger"><i class="bi bi-exclamation-triangle me-1"></i>Lỗi khi tải lịch sử đồng bộ.</td></tr>');
            }
        });
    };

    // Thực hiện đồng bộ hóa đơn ngầm
    window.executeDirectGovSync = function () {
        var direction = $('input[name="syncInvoiceDirection"]:checked').val() || 'All';
        var category = $('input[name="syncInvoiceCategory"]:checked').val() || 'All';
        var fromDate = ($('#syncFromDate').val() || '').trim();
        var toDate = ($('#syncToDate').val() || '').trim();
        var rangeText = ($('#syncDateRange').val() || '').trim();

        if ((!fromDate || !toDate) && rangeText) {
            var parts = rangeText.split(/đến|to|-/);
            if (parts.length >= 2) {
                fromDate = parts[0].trim();
                toDate = parts[1].trim();
            } else if (parts.length === 1 && parts[0].trim()) {
                fromDate = parts[0].trim();
                toDate = parts[0].trim();
            }
        }

        if (!fromDate || !toDate) {
            alert('Vui lòng chọn thời gian đồng bộ.');
            return;
        }

        // Khóa các nút và hiển thị tiến trình
        $('#btnSubmitSyncGov').prop('disabled', true).html('<span class="spinner-border spinner-border-sm me-1.5" role="status"></span> Đang đồng bộ...');
        $('#btnDeleteSyncData').prop('disabled', true);
        $('#btnCancelSync').prop('disabled', true);

        $('#syncLiveProgressBox').removeClass('d-none');
        $('#syncLiveSpinner').removeClass('d-none');
        $('#syncLiveSuccessIcon').addClass('d-none');
        $('#syncLiveErrorIcon').addClass('d-none');
        $('#syncLiveProgressBar').css('width', '10%')
            .removeClass('bg-danger bg-success')
            .addClass('progress-bar-animated progress-bar-striped bg-teal')
            .css('background-color', '#0d9488');
        $('#syncLivePercentBadge').text('10%').removeClass('bg-danger bg-success').addClass('bg-teal');
        $('#syncLiveStageText').text('Đang tự động kết nối Cổng Tổng cục Thuế và giải Captcha AI ngầm...');
        $('#syncLiveStatsText').text('Khởi chạy tiến trình đồng bộ ngầm...');
        $('#syncModalAlert').addClass('d-none').text('');

        var payload = {
            FromDate: fromDate,
            ToDate: toDate,
            InvoiceType: direction,
            CategoryType: category
        };

        $.ajax({
            url: '/Import/AutoSync',
            type: 'POST',
            contentType: 'application/json',
            headers: { "RequestVerificationToken": getCsrfToken() },
            data: JSON.stringify(payload),
            success: function (res) {
                if (res.needSetup) {
                    resetSyncButtons();
                    $('#syncLiveProgressBox').addClass('d-none');
                    promptForInitialPassword(res.message);
                    return;
                }

                if (res.success && res.jobId) {
                    activeSyncJobId = res.jobId;
                    startSyncPolling(res.jobId);
                } else {
                    resetSyncButtons();
                    $('#syncLiveProgressBox').addClass('d-none');
                    $('#syncModalAlert').removeClass('d-none alert-success').addClass('alert-danger')
                        .html('<i class="bi bi-exclamation-circle me-1"></i> ' + (res.message || 'Lỗi khi khởi chạy đồng bộ hóa đơn.'));
                }
            },
            error: function (xhr) {
                resetSyncButtons();
                $('#syncLiveProgressBox').addClass('d-none');
                $('#syncModalAlert').removeClass('d-none alert-success').addClass('alert-danger')
                    .html('<i class="bi bi-exclamation-circle me-1"></i> Lỗi kết nối máy chủ khi gọi đồng bộ Cổng Thuế.');
            }
        });
    };

    // Theo dõi tiến trình đồng bộ ngầm
    function startSyncPolling(jobId) {
        if (syncPollingTimer) clearInterval(syncPollingTimer);

        syncPollingTimer = setInterval(function () {
            $.ajax({
                url: '/Import/RemoteStatus?jobId=' + encodeURIComponent(jobId),
                type: 'GET',
                cache: false,
                success: function (res) {
                    if (!res.success) return;

                    var percent = Math.max(10, Math.min(100, res.percent || 10));
                    $('#syncLiveProgressBar').css('width', percent + '%');
                    $('#syncLivePercentBadge').text(percent + '%');

                    if (res.stage) {
                        $('#syncLiveStageText').text(res.stage);
                    }

                    var stats = [];
                    if (res.totalFound > 0) stats.push('Tìm thấy ' + res.totalFound + ' HĐ');
                    if (res.newCount > 0) stats.push(res.newCount + ' mới');
                    if (res.duplicateCount > 0) stats.push(res.duplicateCount + ' đã đủ file');
                    if (res.failedCount > 0) stats.push(res.failedCount + ' lỗi');

                    if (stats.length > 0) {
                        $('#syncLiveStatsText').text(stats.join(' • '));
                    }

                    if (res.status === 'Completed') {
                        clearInterval(syncPollingTimer);
                        syncPollingTimer = null;
                        activeSyncJobId = null;

                        // 1. TẮT HOÀN TOÀN BIỂU TƯỢNG QUAY VÒNG TRÒN VÀ THAY BẰNG DẤU TÍCH XANH
                        $('#syncLiveSpinner').addClass('d-none');
                        $('#syncLiveSuccessIcon').removeClass('d-none');
                        $('#syncLiveErrorIcon').addClass('d-none');

                        // 2. Tắt animation chuyển động của thanh progress bar
                        $('#syncLiveProgressBar').css('width', '100%')
                            .removeClass('progress-bar-animated progress-bar-striped bg-teal')
                            .addClass('bg-success');
                        $('#syncLivePercentBadge').text('100%').removeClass('bg-teal').addClass('bg-success');
                        $('#syncLiveStageText').text('Đồng bộ hoàn tất thành công!');

                        resetSyncButtons();
                        loadSyncHistory(true);

                        var summaryMsg = res.stage || 'Đã đồng bộ xong toàn bộ hóa đơn từ Cổng Tổng cục Thuế.';
                        $('#syncModalAlert').removeClass('d-none alert-danger').addClass('alert-success')
                            .html('<i class="bi bi-check-circle-fill me-1"></i> <strong>Đồng bộ hóa đơn thành công!</strong> ' + summaryMsg);

                        // Cuộn lên đầu modal để người dùng thấy thông báo
                        $('#modalSyncInvoices .modal-body').animate({ scrollTop: 0 }, 200);

                        // 3. THÔNG BÁO RÕ RÀNG BẰNG POPUP (KHÔNG ĐỂ NGƯỜI DÙNG PHẢI ĐỢI QUAY VÒNG NỮA)
                        setTimeout(function () {
                            alert('🎉 ĐỒNG BỘ HÓA ĐƠN THÀNH CÔNG!\n\n' + summaryMsg);
                        }, 200);

                        // Tự động làm mới bảng hóa đơn trên màn hình chính nếu có
                        if (typeof window.applyInvoiceFilter === 'function') {
                            window.applyInvoiceFilter();
                        } else if (typeof window.reloadInvoicesTable === 'function') {
                            window.reloadInvoicesTable();
                        }
                    } else if (res.status === 'Failed') {
                        clearInterval(syncPollingTimer);
                        syncPollingTimer = null;
                        activeSyncJobId = null;

                        // Tắt hoàn toàn spinner và thay bằng dấu X đỏ
                        $('#syncLiveSpinner').addClass('d-none');
                        $('#syncLiveSuccessIcon').addClass('d-none');
                        $('#syncLiveErrorIcon').removeClass('d-none');

                        $('#syncLiveProgressBar').removeClass('progress-bar-animated progress-bar-striped bg-teal').addClass('bg-danger');
                        $('#syncLivePercentBadge').removeClass('bg-teal').addClass('bg-danger');
                        $('#syncLiveStageText').text('Tiến trình đồng bộ thất bại');

                        resetSyncButtons();
                        loadSyncHistory(true);

                        var errMsg = res.stage || 'Lỗi khi đồng bộ hóa đơn từ Cổng Thuế.';
                        $('#syncModalAlert').removeClass('d-none alert-success').addClass('alert-danger')
                            .html('<i class="bi bi-exclamation-triangle-fill me-1"></i> ' + errMsg);

                        $('#modalSyncInvoices .modal-body').animate({ scrollTop: 0 }, 200);

                        setTimeout(function () {
                            alert('⚠️ ĐỒNG BỘ THẤT BẠI: ' + errMsg);
                        }, 200);
                    }
                },
                error: function () {
                    // Tiếp tục đợi
                }
            });
        }, 800);
    }

    // Xóa dữ liệu hóa đơn đã đồng bộ trong khoảng ngày đã chọn
    window.confirmDeleteSyncData = function () {
        var compName = $('#syncModalCompanyName').text() || 'công ty hiện tại';
        var fromDate = ($('#syncFromDate').val() || '').trim();
        var toDate = ($('#syncToDate').val() || '').trim();
        var direction = $('input[name="syncInvoiceDirection"]:checked').val() || 'All';

        var dirText = 'tất cả hóa đơn mua vào & bán ra';
        if (direction === 'BanRa') dirText = 'hóa đơn bán ra';
        if (direction === 'MuaVao') dirText = 'hóa đơn mua vào';

        var confirmMsg = 'Bạn có chắc chắn muốn XÓA DỮ LIỆU ĐÃ ĐỒNG BỘ (' + dirText + ') của ' + compName + ' trong khoảng thời gian từ ' + fromDate + ' đến ' + toDate + ' không?\n\nThao tác này sẽ xóa các hóa đơn và tệp tương ứng trong khoảng ngày này để bạn có thể đồng bộ lại sạch sẽ từ Cổng Thuế.';

        if (!confirm(confirmMsg)) {
            return;
        }

        $('#btnDeleteSyncData').prop('disabled', true).html('<span class="spinner-border spinner-border-sm me-1" role="status"></span> Đang xóa...');

        $.ajax({
            url: '/Import/DeleteSyncData',
            type: 'POST',
            contentType: 'application/json',
            headers: { "RequestVerificationToken": getCsrfToken() },
            data: JSON.stringify({
                FromDate: fromDate,
                ToDate: toDate,
                InvoiceType: direction
            }),
            success: function (res) {
                $('#btnDeleteSyncData').prop('disabled', false).html('<i class="bi bi-trash"></i> Xóa dữ liệu đã đồng bộ');
                if (res && res.success) {
                    $('#syncModalAlert').removeClass('d-none alert-danger').addClass('alert-success')
                        .html('<i class="bi bi-check-circle me-1"></i> ' + res.message);
                    loadSyncHistory(true);

                    // Làm mới bảng danh sách bên ngoài nếu có
                    if (typeof window.applyInvoiceFilter === 'function') {
                        window.applyInvoiceFilter();
                    } else if (typeof window.reloadInvoicesTable === 'function') {
                        window.reloadInvoicesTable();
                    }
                } else {
                    $('#syncModalAlert').removeClass('d-none alert-success').addClass('alert-danger')
                        .html('<i class="bi bi-exclamation-circle me-1"></i> ' + (res.message || 'Lỗi khi xóa dữ liệu đã đồng bộ.'));
                }
            },
            error: function () {
                $('#btnDeleteSyncData').prop('disabled', false).html('<i class="bi bi-trash"></i> Xóa dữ liệu đã đồng bộ');
                $('#syncModalAlert').removeClass('d-none alert-success').addClass('alert-danger')
                    .html('<i class="bi bi-exclamation-circle me-1"></i> Lỗi kết nối khi gửi yêu cầu xóa dữ liệu.');
            }
        });
    };

    // Khi công ty chưa từng lưu mật khẩu Cổng Thuế một lần nào
    function promptForInitialPassword(msg) {
        var compName = $('#syncModalCompanyName').text() || 'doanh nghiệp';
        var promptMsg = 'Doanh nghiệp [' + compName + '] chưa lưu mật khẩu Cổng Thuế.\n\nVui lòng nhập mật khẩu Cổng Thuế 1 lần duy nhất để hệ thống lưu và tự động kết nối ngầm vĩnh viễn:';
        var pass = prompt(promptMsg, '');
        if (!pass) return;

        $.ajax({
            url: '/Import/SaveGovConfig',
            type: 'POST',
            contentType: 'application/json',
            headers: { "RequestVerificationToken": getCsrfToken() },
            data: JSON.stringify({
                Password: pass
            }),
            success: function (res) {
                if (res && res.success) {
                    alert('Đã lưu mật khẩu Cổng Thuế thành công! Hệ thống sẽ tự động đồng bộ ngay bây giờ.');
                    executeDirectGovSync();
                } else {
                    alert(res.message || 'Lỗi khi lưu mật khẩu Cổng Thuế.');
                }
            },
            error: function () {
                alert('Lỗi kết nối khi lưu mật khẩu Cổng Thuế.');
            }
        });
    }

    function resetSyncButtons() {
        $('#btnSubmitSyncGov').prop('disabled', false).html('<i class="bi bi-arrow-repeat me-1"></i> Đồng bộ');
        $('#btnDeleteSyncData').prop('disabled', false);
        $('#btnCancelSync').prop('disabled', false);
    }
})();

