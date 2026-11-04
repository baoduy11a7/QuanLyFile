/**
 * original-invoice-handler.js
 * Module xử lý Cấu hình thông tin lấy hóa đơn gốc & Tiến trình tải hàng loạt
 * FDI Accounting and Tax Pro v3.3.16
 */

(function (window, $) {
    'use strict';

    // State lưu trữ phiên làm việc của Batch Download
    var batchDownloadState = {
        invoices: [],
        total: 0,
        success: 0,
        processing: 0,
        skipped: 0,
        failed: 0,
        completed: 0,
        activeWorkers: 0,
        maxConcurrency: 2,
        queueIndex: 0,
        isCancelled: false,
        processedIds: []
    };

    /**
     * Mở Modal Cấu hình thông tin lấy hóa đơn gốc
     */
    window.openOriginalInvoiceConfigModal = function () {
        // Đóng dropdown nếu có
        try {
            var dropdownToggle = document.querySelector('[data-bs-toggle="dropdown"][aria-expanded="true"]');
            if (dropdownToggle && window.bootstrap && bootstrap.Dropdown) {
                var dd = bootstrap.Dropdown.getOrCreateInstance(dropdownToggle);
                if (dd) dd.hide();
            }
        } catch (e) { }

        $('#origCfg_Alert').addClass('d-none').html('');
        $('#origCfg_CompanySub').text('Đang tải cấu hình...');

        // Gọi API nạp cấu hình đã lưu
        $.get('/TaxAccount/GetOriginalInvoiceConfig', function (res) {
            if (res && res.success && res.data) {
                var c = res.data;
                $('#origCfg_TaxAccountId').val(c.taxAccountId || '');
                $('#origCfg_CompanySub').text((c.companyName || 'Doanh nghiệp') + ' (MST: ' + (c.buyerTaxCode || c.username || '') + ')');

                // Tab A
                $('#origCfg_ApiUrl').val(c.apiUrl || 'https://hoadondientu.gdt.gov.vn');
                $('#origCfg_AuthMethod').val(c.authMethod || 'AutoOcrCaptcha');
                $('#origCfg_Username').val(c.username || '');
                $('#origCfg_Password').val('');

                if (c.hasSavedPassword) {
                    $('#origCfg_SavedPassText').html('<span class="text-success fw-semibold"><i class="bi bi-check-circle-fill me-1"></i>Đã lưu mật khẩu Cổng Thuế (Để trống nếu giữ nguyên).</span>');
                } else {
                    $('#origCfg_SavedPassText').text('Chưa lưu mật khẩu. Vui lòng nhập mật khẩu Cổng Thuế.');
                }

                if (c.connectionStatus === 'Đã kết nối' || c.hasSavedPassword) {
                    $('#origCfg_StatusBadge').removeClass('bg-secondary-subtle text-secondary bg-danger-subtle text-danger').addClass('bg-success-subtle text-success border-success-subtle').html('<i class="bi bi-check-circle-fill me-1"></i>' + (c.connectionStatus || 'Đã kết nối'));
                } else {
                    $('#origCfg_StatusBadge').removeClass('bg-success-subtle text-success').addClass('bg-secondary-subtle text-secondary').text(c.connectionStatus || 'Chưa kết nối');
                }

                $('#origCfg_LastConnectedAt').text(c.lastConnectedAt || 'Chưa có lịch sử kết nối');

                // Tab B
                $('#origCfg_BuyerTaxCode').val(c.buyerTaxCode || '');
                $('#origCfg_BuyerCccd').val(c.buyerCccd || '');
                $('#origCfg_DateRangeType').val(c.dateRangeType || 'CurrentMonth');
                if (c.fromDate) $('#origCfg_FromDate').val(c.fromDate.substring(0, 10));
                if (c.toDate) $('#origCfg_ToDate').val(c.toDate.substring(0, 10));
                $('#origCfg_InvoiceStatus').val(c.invoiceStatus || 'All');
                $('#origCfg_InvoiceType').val(c.invoiceType || 'Purchase');
                $('#origCfg_OnlyUnDownloaded').prop('checked', c.onlyUnDownloaded !== false);
                $('#origCfg_SkipExistingFiles').prop('checked', c.skipExistingFiles !== false);
                $('#origCfg_AllowReDownload').prop('checked', c.allowReDownload !== false);

                // Tab C
                $('#origCfg_DownloadXml').prop('checked', c.downloadXml !== false);
                $('#origCfg_DownloadPdf').prop('checked', c.downloadPdf !== false);
                $('#origCfg_DownloadBoth').prop('checked', c.downloadBoth !== false);

                // Tab D
                $('#origCfg_StorageFolder').val(c.storageFolder || 'HoaDonGoc');
                $('#origCfg_FileNamePattern').val(c.fileNamePattern || '{KHHDon}_{SHDon}_{MSTBan}');
                $('#origCfg_AutoOrganizeByMst').prop('checked', c.autoOrganizeByMst !== false);
                $('#origCfg_AutoOrganizeByYearMonth').prop('checked', c.autoOrganizeByYearMonth !== false);
                $('#origCfg_AutoOrganizeBySellerMst').prop('checked', c.autoOrganizeBySellerMst === true);
                $('#origCfg_OverwriteExistingFiles').prop('checked', c.overwriteExistingFiles === true);

                // Tab E
                $('#origCfg_AutoDownloadAfterSync').prop('checked', c.autoDownloadAfterSync !== false);
                $('#origCfg_AutoRetryFailed').prop('checked', c.autoRetryFailed !== false);
                $('#origCfg_MaxRetryCount').val(c.maxRetryCount || 3);
                $('#origCfg_RetryDelaySeconds').val(c.retryDelaySeconds || 3);
                $('#origCfg_EnableAuditLog').prop('checked', c.enableAuditLog !== false);
            }
        });

        // Hiển thị Modal
        var modalEl = document.getElementById('modalOriginalInvoiceConfig');
        if (modalEl) {
            try {
                var modal = bootstrap.Modal.getOrCreateInstance(modalEl);
                modal.show();
            } catch (err) {
                $(modalEl).modal('show');
            }
        }
    };

    /**
     * Ẩn / Hiện mật khẩu Cổng Thuế
     */
    window.toggleOrigPassVisibility = function () {
        var inp = document.getElementById('origCfg_Password');
        var icon = document.getElementById('origCfg_PassIcon');
        if (inp.type === 'password') {
            inp.type = 'text';
            icon.className = 'bi bi-eye-slash';
        } else {
            inp.type = 'password';
            icon.className = 'bi bi-eye';
        }
    };

    /**
     * Thay đổi khoảng ngày mặc định
     */
    window.onOrigDateRangeChanged = function (val) {
        var now = new Date();
        var y = now.getFullYear();
        var m = now.getMonth();

        if (val === 'CurrentMonth') {
            var firstDay = new Date(y, m, 1);
            var lastDay = new Date(y, m + 1, 0);
            $('#origCfg_FromDate').val(formatDateToInput(firstDay));
            $('#origCfg_ToDate').val(formatDateToInput(lastDay));
        } else if (val === 'CurrentQuarter') {
            var qStartMonth = Math.floor(m / 3) * 3;
            var qStart = new Date(y, qStartMonth, 1);
            var qEnd = new Date(y, qStartMonth + 3, 0);
            $('#origCfg_FromDate').val(formatDateToInput(qStart));
            $('#origCfg_ToDate').val(formatDateToInput(qEnd));
        } else if (val === 'AllYear') {
            $('#origCfg_FromDate').val(y + '-01-01');
            $('#origCfg_ToDate').val(y + '-12-31');
        }
    };

    function formatDateToInput(d) {
        var month = '' + (d.getMonth() + 1);
        var day = '' + d.getDate();
        var year = d.getFullYear();
        if (month.length < 2) month = '0' + month;
        if (day.length < 2) day = '0' + day;
        return [year, month, day].join('-');
    }

    /**
     * Kiểm tra kết nối hệ thống GOV
     */
    window.testGovConnectionAction = function () {
        var btn = $('#btnTestGovConnection');
        var icon = $('#origCfg_TestIcon');
        var user = $('#origCfg_Username').val();
        var pass = $('#origCfg_Password').val();

        btn.prop('disabled', true);
        icon.removeClass('bi-arrow-repeat').addClass('spinner-border spinner-border-sm');
        $('#origCfg_Alert').removeClass('d-none alert-success alert-danger alert-info').addClass('alert-info')
            .html('<div class="d-flex align-items-center"><div class="spinner-border spinner-border-sm me-2"></div> Đang kết nối Cổng Tổng cục Thuế và tự động giải mã Captcha bằng AI OCR...</div>');

        var token = $('input[name="__RequestVerificationToken"]').first().val();

        $.ajax({
            url: '/TaxAccount/TestOriginalConnection',
            type: 'POST',
            data: {
                username: user,
                password: pass,
                __RequestVerificationToken: token
            },
            success: function (res) {
                btn.prop('disabled', false);
                icon.removeClass('spinner-border spinner-border-sm').addClass('bi-arrow-repeat');

                if (res && res.success) {
                    $('#origCfg_Alert').removeClass('alert-info alert-danger').addClass('alert-success')
                        .html('<i class="bi bi-check-circle-fill me-1"></i> ' + res.message);
                    $('#origCfg_StatusBadge').removeClass('bg-secondary-subtle text-secondary bg-danger-subtle text-danger').addClass('bg-success-subtle text-success border-success-subtle')
                        .html('<i class="bi bi-check-circle-fill me-1"></i> Đã kết nối');
                    if (res.lastConnectedAt) {
                        $('#origCfg_LastConnectedAt').text(res.lastConnectedAt);
                    }
                } else {
                    $('#origCfg_Alert').removeClass('alert-info alert-success').addClass('alert-danger')
                        .html('<i class="bi bi-exclamation-triangle-fill me-1"></i> ' + (res.message || 'Kiểm tra kết nối thất bại.'));
                    $('#origCfg_StatusBadge').removeClass('bg-success-subtle text-success').addClass('bg-danger-subtle text-danger')
                        .html('<i class="bi bi-x-circle-fill me-1"></i> Lỗi kết nối');
                }
            },
            error: function () {
                btn.prop('disabled', false);
                icon.removeClass('spinner-border spinner-border-sm').addClass('bi-arrow-repeat');
                $('#origCfg_Alert').removeClass('alert-info alert-success').addClass('alert-danger')
                    .html('<i class="bi bi-exclamation-triangle-fill me-1"></i> Lỗi kết nối tới máy chủ.');
            }
        });
    };

    /**
     * Khôi phục cấu hình về mặc định ban đầu
     */
    window.resetOrigConfigToDefault = function () {
        if (!confirm('Bạn có chắc chắn muốn khôi phục toàn bộ cấu hình lấy hóa đơn gốc về thiết lập mặc định chuẩn của phần mềm không?')) {
            return;
        }

        var token = $('input[name="__RequestVerificationToken"]').first().val();
        var accountId = $('#origCfg_TaxAccountId').val();

        $.ajax({
            url: '/TaxAccount/ResetOriginalInvoiceConfig',
            type: 'POST',
            data: {
                taxAccountId: accountId,
                __RequestVerificationToken: token
            },
            success: function (res) {
                if (res && res.success) {
                    $('#origCfg_Alert').removeClass('d-none alert-danger alert-info').addClass('alert-success')
                        .html('<i class="bi bi-check-circle-fill me-1"></i> ' + res.message);
                    setTimeout(function () {
                        window.openOriginalInvoiceConfigModal();
                    }, 800);
                } else {
                    alert(res.message || 'Lỗi khi khôi phục cấu hình.');
                }
            }
        });
    };

    /**
     * Lưu cấu hình thông tin lấy hóa đơn gốc
     */
    window.submitOriginalInvoiceConfig = function (e) {
        e.preventDefault();
        var btn = $('#btnSaveOrigConfigModal');
        btn.prop('disabled', true).html('<span class="spinner-border spinner-border-sm me-1"></span> Đang lưu...');

        var form = $('#formOriginalInvoiceConfig');

        $.ajax({
            url: '/TaxAccount/SaveOriginalInvoiceConfig',
            type: 'POST',
            data: form.serialize(),
            success: function (res) {
                btn.prop('disabled', false).html('<i class="bi bi-check-circle-fill me-1"></i> Lưu cấu hình');
                if (res && res.success) {
                    $('#origCfg_Alert').removeClass('d-none alert-danger alert-info').addClass('alert-success')
                        .html('<i class="bi bi-check-circle-fill me-1"></i> ' + res.message);

                    setTimeout(function () {
                        var modalEl = document.getElementById('modalOriginalInvoiceConfig');
                        if (modalEl && window.bootstrap && bootstrap.Modal) {
                            var modal = bootstrap.Modal.getInstance(modalEl);
                            if (modal) modal.hide();
                        } else {
                            $(modalEl).modal('hide');
                        }
                    }, 1200);
                } else {
                    $('#origCfg_Alert').removeClass('d-none alert-success alert-info').addClass('alert-danger')
                        .html('<i class="bi bi-exclamation-triangle-fill me-1"></i> ' + (res.message || 'Lỗi khi lưu cấu hình.'));
                }
            },
            error: function () {
                btn.prop('disabled', false).html('<i class="bi bi-check-circle-fill me-1"></i> Lưu cấu hình');
                $('#origCfg_Alert').removeClass('d-none alert-success alert-info').addClass('alert-danger')
                    .html('<i class="bi bi-exclamation-triangle-fill me-1"></i> Lỗi kết nối tới máy chủ khi lưu.');
            }
        });
    };


    // =========================================================================
    // SECTION 4 & 5: TIẾN TRÌNH TẢI HÀNG LOẠT HÓA ĐƠN GỐC (BATCH DOWNLOAD)
    // =========================================================================

    /**
     * Kích hoạt tiến trình Tải hàng loạt hóa đơn gốc
     */
    window.triggerBatchOriginalDownload = function () {
        var invoiceItems = [];

        // 1. Kiểm tra nếu có các dòng được chọn (checkbox)
        var checkedBoxes = $('.row-select:checked');
        if (checkedBoxes.length > 0) {
            checkedBoxes.each(function () {
                var row = $(this).closest('tr');
                invoiceItems.push(extractInvoiceDataFromRow(row, $(this).val()));
            });
        } else {
            // Nếu không chọn dòng nào, lấy toàn bộ hóa đơn đang hiển thị trên bảng
            var allRows = $('.row-select');
            if (allRows.length > 0) {
                allRows.each(function () {
                    var row = $(this).closest('tr');
                    invoiceItems.push(extractInvoiceDataFromRow(row, $(this).val()));
                });
            }
        }

        if (invoiceItems.length === 0) {
            alert('Không tìm thấy hóa đơn nào để tải tệp gốc.');
            return;
        }

        // Khởi tạo state
        batchDownloadState.invoices = invoiceItems;
        batchDownloadState.total = invoiceItems.length;
        batchDownloadState.success = 0;
        batchDownloadState.processing = 0;
        batchDownloadState.skipped = 0;
        batchDownloadState.failed = 0;
        batchDownloadState.completed = 0;
        batchDownloadState.queueIndex = 0;
        batchDownloadState.isCancelled = false;
        batchDownloadState.processedIds = [];

        // Cập nhật giao diện modal tiến trình
        updateBatchStatsUi();
        $('#batchOrig_StatusText').text('Bắt đầu tải hàng loạt hóa đơn gốc...');
        $('#batchOrig_ProgressBar').css('width', '0%').removeClass('bg-success bg-danger').addClass('bg-primary progress-bar-animated');
        $('#btnDownloadBatchZipResult').prop('disabled', true);
        $('#btnBatchOrigRetryFailed').addClass('d-none');

        // Render danh sách vào bảng kết quả
        renderBatchTableInitial();

        // Mở Modal tiến trình
        var modalEl = document.getElementById('modalBatchOriginalDownloadProgress');
        if (modalEl) {
            try {
                var modal = bootstrap.Modal.getOrCreateInstance(modalEl);
                modal.show();
            } catch (err) {
                $(modalEl).modal('show');
            }
        }

        // Bắt đầu chạy các worker song song (concurrency: 2)
        startBatchWorkers();
    };

    function extractInvoiceDataFromRow(row, id) {
        var num = row.find('td:nth-child(4)').text().trim() || row.attr('data-number') || '';
        var sym = row.find('td:nth-child(3)').text().trim() || row.attr('data-symbol') || '';
        var sellerTax = row.find('td:nth-child(6)').text().trim() || row.attr('data-sellertax') || '';
        var sellerName = row.find('td:nth-child(7)').text().trim() || row.attr('data-sellername') || '';

        return {
            id: parseInt(id),
            number: num,
            symbol: sym,
            sellerTax: sellerTax,
            sellerName: sellerName
        };
    }

    function renderBatchTableInitial() {
        var tbody = $('#batchOrig_TableBody');
        tbody.empty();

        batchDownloadState.invoices.forEach(function (inv, idx) {
            var tr = $('<tr id="batchRow_' + inv.id + '">' +
                '<td class="text-center text-muted">' + (idx + 1) + '</td>' +
                '<td class="text-center font-monospace fw-semibold">' + (inv.number || '---') + '</td>' +
                '<td class="text-center font-monospace">' + (inv.symbol || '---') + '</td>' +
                '<td class="font-monospace small">' + (inv.sellerTax || '---') + '</td>' +
                '<td class="text-truncate" style="max-width: 220px;" title="' + inv.sellerName + '">' + (inv.sellerName || '---') + '</td>' +
                '<td class="text-center" id="statusCol_' + inv.id + '"><span class="badge bg-secondary-subtle text-secondary border">Chờ xử lý</span></td>' +
                '<td class="text-center text-muted" id="formatCol_' + inv.id + '">---</td>' +
                '<td class="small text-muted text-truncate" style="max-width: 200px;" id="pathCol_' + inv.id + '">Đang chờ lượt tải...</td>' +
                '<td class="text-center" id="actionCol_' + inv.id + '">---</td>' +
                '</tr>');
            tbody.append(tr);
        });
    }

    function startBatchWorkers() {
        for (var i = 0; i < batchDownloadState.maxConcurrency; i++) {
            processNextBatchItem();
        }
    }

    function processNextBatchItem() {
        if (batchDownloadState.isCancelled) return;
        if (batchDownloadState.queueIndex >= batchDownloadState.total) {
            checkBatchCompletion();
            return;
        }

        var currentIndex = batchDownloadState.queueIndex++;
        var item = batchDownloadState.invoices[currentIndex];
        if (!item) {
            checkBatchCompletion();
            return;
        }

        batchDownloadState.processing++;
        updateBatchStatsUi();

        // Cập nhật trạng thái đang tải trên bảng
        $('#statusCol_' + item.id).html('<span class="badge bg-primary-subtle text-primary border"><div class="spinner-border spinner-border-sm me-1" style="width:0.7rem;height:0.7rem;"></div>Đang tải...</span>');
        $('#pathCol_' + item.id).html('<span class="text-primary"><i class="bi bi-hourglass-split me-1"></i>Đang gọi API Tổng cục Thuế...</span>');

        var token = $('input[name="__RequestVerificationToken"]').first().val();

        $.ajax({
            url: '/Invoice/ProcessSingleOriginalDownload',
            type: 'POST',
            data: {
                invoiceId: item.id,
                forceReDownload: false,
                __RequestVerificationToken: token
            },
            success: function (res) {
                batchDownloadState.processing--;
                batchDownloadState.completed++;

                if (res && res.isSuccess) {
                    batchDownloadState.processedIds.push(item.id);

                    if (res.isSkipped) {
                        batchDownloadState.skipped++;
                        $('#statusCol_' + item.id).html('<span class="badge bg-warning-subtle text-warning border-warning-subtle"><i class="bi bi-check2-all me-1"></i>Đã có sẵn</span>');
                    } else {
                        batchDownloadState.success++;
                        $('#statusCol_' + item.id).html('<span class="badge bg-success-subtle text-success border-success-subtle"><i class="bi bi-check-circle-fill me-1"></i>Thành công</span>');
                    }

                    $('#formatCol_' + item.id).html('<span class="badge bg-light border text-dark font-monospace">' + (res.formats || 'XML, PDF') + '</span>');
                    $('#pathCol_' + item.id).html('<span class="text-success small text-truncate d-inline-block" style="max-width: 240px;" title="' + (res.savedPath || '') + '"><i class="bi bi-folder-check me-1"></i>' + (res.savedPath || 'Đã lưu') + '</span>');
                    $('#actionCol_' + item.id).html('<button type="button" class="btn btn-outline-secondary btn-sm py-0 px-1.5" style="font-size:0.7rem;" onclick="retrySingleBatchItemAction(' + item.id + ')" title="Tải lại file gốc"><i class="bi bi-arrow-repeat"></i></button>');
                } else {
                    batchDownloadState.failed++;
                    var errText = (res && res.errorMessage) ? res.errorMessage : 'Không thể tải file gốc từ CQT';
                    $('#statusCol_' + item.id).html('<span class="badge bg-danger-subtle text-danger border-danger-subtle"><i class="bi bi-x-circle-fill me-1"></i>Thất bại</span>');
                    $('#pathCol_' + item.id).html('<span class="text-danger small" title="' + errText + '"><i class="bi bi-exclamation-triangle me-1"></i>' + errText + '</span>');
                    $('#actionCol_' + item.id).html('<button type="button" class="btn btn-outline-danger btn-sm py-0 px-2 fw-semibold" style="font-size:0.72rem;" onclick="retrySingleBatchItemAction(' + item.id + ')"><i class="bi bi-arrow-repeat me-1"></i>Thử lại</button>');
                }

                updateBatchStatsUi();
                processNextBatchItem();
            },
            error: function () {
                batchDownloadState.processing--;
                batchDownloadState.completed++;
                batchDownloadState.failed++;

                $('#statusCol_' + item.id).html('<span class="badge bg-danger-subtle text-danger border-danger-subtle"><i class="bi bi-x-circle-fill me-1"></i>Lỗi mạng</span>');
                $('#pathCol_' + item.id).html('<span class="text-danger small"><i class="bi bi-exclamation-triangle me-1"></i>Lỗi kết nối máy chủ</span>');
                $('#actionCol_' + item.id).html('<button type="button" class="btn btn-outline-danger btn-sm py-0 px-2 fw-semibold" style="font-size:0.72rem;" onclick="retrySingleBatchItemAction(' + item.id + ')"><i class="bi bi-arrow-repeat me-1"></i>Thử lại</button>');

                updateBatchStatsUi();
                processNextBatchItem();
            }
        });
    }

    function updateBatchStatsUi() {
        $('#stat_total').text(batchDownloadState.total);
        $('#stat_success').text(batchDownloadState.success);
        $('#stat_processing').text(batchDownloadState.processing);
        $('#stat_skipped').text(batchDownloadState.skipped);
        $('#stat_failed').text(batchDownloadState.failed);

        var percent = 0;
        if (batchDownloadState.total > 0) {
            percent = Math.round((batchDownloadState.completed / batchDownloadState.total) * 100);
        }
        $('#batchOrig_PercentText').text(percent + '%');
        $('#batchOrig_ProgressBar').css('width', percent + '%');

        if (batchDownloadState.processing > 0) {
            $('#batchOrig_StatusText').text('Đang tải hóa đơn gốc (' + batchDownloadState.completed + '/' + batchDownloadState.total + ')...');
        }
    }

    function checkBatchCompletion() {
        if (batchDownloadState.completed >= batchDownloadState.total) {
            $('#batchOrig_ProgressBar').removeClass('progress-bar-animated bg-primary').addClass('bg-success');
            $('#batchOrig_StatusText').html('<span class="text-success fw-bold"><i class="bi bi-check-circle-fill me-1"></i>Hoàn thành tiến trình tải hàng loạt hóa đơn gốc!</span>');

            if (batchDownloadState.success > 0 || batchDownloadState.skipped > 0) {
                $('#btnDownloadBatchZipResult').prop('disabled', false);
            }

            if (batchDownloadState.failed > 0) {
                $('#badgeFailedCount').text(batchDownloadState.failed);
                $('#btnBatchOrigRetryFailed').removeClass('d-none');
            } else {
                $('#btnBatchOrigRetryFailed').addClass('d-none');
            }
        }
    }

    /**
     * Tải lại riêng 1 dòng bị lỗi
     */
    window.retrySingleBatchItemAction = function (invoiceId) {
        var token = $('input[name="__RequestVerificationToken"]').first().val();

        $('#statusCol_' + invoiceId).html('<span class="badge bg-primary-subtle text-primary border"><div class="spinner-border spinner-border-sm me-1" style="width:0.7rem;height:0.7rem;"></div>Đang thử lại...</span>');
        $('#pathCol_' + invoiceId).html('<span class="text-primary"><i class="bi bi-hourglass-split me-1"></i>Đang gọi lại Cổng Thuế...</span>');

        $.ajax({
            url: '/Invoice/ProcessSingleOriginalDownload',
            type: 'POST',
            data: {
                invoiceId: invoiceId,
                forceReDownload: true,
                __RequestVerificationToken: token
            },
            success: function (res) {
                if (res && res.isSuccess) {
                    if (batchDownloadState.failed > 0) batchDownloadState.failed--;
                    batchDownloadState.success++;
                    batchDownloadState.processedIds.push(invoiceId);

                    $('#statusCol_' + invoiceId).html('<span class="badge bg-success-subtle text-success border-success-subtle"><i class="bi bi-check-circle-fill me-1"></i>Thành công</span>');
                    $('#formatCol_' + invoiceId).html('<span class="badge bg-light border text-dark font-monospace">' + (res.formats || 'XML, PDF') + '</span>');
                    $('#pathCol_' + invoiceId).html('<span class="text-success small text-truncate d-inline-block" style="max-width: 240px;" title="' + (res.savedPath || '') + '"><i class="bi bi-folder-check me-1"></i>' + (res.savedPath || 'Đã lưu') + '</span>');
                    $('#actionCol_' + invoiceId).html('<button type="button" class="btn btn-outline-secondary btn-sm py-0 px-1.5" style="font-size:0.7rem;" onclick="retrySingleBatchItemAction(' + invoiceId + ')"><i class="bi bi-arrow-repeat"></i></button>');
                } else {
                    var errText = (res && res.errorMessage) ? res.errorMessage : 'Thử lại thất bại.';
                    $('#statusCol_' + invoiceId).html('<span class="badge bg-danger-subtle text-danger border-danger-subtle"><i class="bi bi-x-circle-fill me-1"></i>Thất bại</span>');
                    $('#pathCol_' + invoiceId).html('<span class="text-danger small" title="' + errText + '"><i class="bi bi-exclamation-triangle me-1"></i>' + errText + '</span>');
                }
                updateBatchStatsUi();
                checkBatchCompletion();
            }
        });
    };

    /**
     * Thử lại tất cả các dòng bị lỗi
     */
    window.retryFailedInvoicesAction = function () {
        var failedIds = [];
        batchDownloadState.invoices.forEach(function (inv) {
            var tr = $('#batchRow_' + inv.id);
            if (tr.find('#statusCol_' + inv.id + ' .bg-danger-subtle').length > 0) {
                failedIds.push(inv);
            }
        });

        if (failedIds.length === 0) return;

        // Đưa các dòng lỗi về hàng đợi
        batchDownloadState.invoices = failedIds;
        batchDownloadState.total = failedIds.length;
        batchDownloadState.completed = 0;
        batchDownloadState.queueIndex = 0;
        batchDownloadState.processing = 0;
        batchDownloadState.failed = 0;

        $('#btnBatchOrigRetryFailed').addClass('d-none');
        updateBatchStatsUi();
        startBatchWorkers();
    };

    /**
     * Tải toàn bộ file nén ZIP chứa các file gốc đã tải
     */
    window.downloadAllProcessedZipAction = function () {
        var ids = batchDownloadState.processedIds;
        if (!ids || ids.length === 0) {
            // Lấy tất cả IDs
            ids = batchDownloadState.invoices.map(function (i) { return i.id; });
        }

        if (ids.length === 0) {
            alert('Chưa có tệp gốc nào để tải xuống.');
            return;
        }

        var form = $('<form method="POST" action="/Invoice/ExportBatchOriginalZip" target="_blank"></form>');
        var token = $('input[name="__RequestVerificationToken"]').first().val();
        form.append('<input type="hidden" name="__RequestVerificationToken" value="' + token + '" />');

        ids.forEach(function (id) {
            form.append('<input type="hidden" name="invoiceIds" value="' + id + '" />');
        });

        $('body').append(form);
        form.submit();
        form.remove();
    };

})(window, jQuery);
